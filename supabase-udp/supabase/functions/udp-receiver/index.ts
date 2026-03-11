/**
 * udp-receiver — Supabase Edge Function
 *
 * Receives a Proxylity UDP batch from API Gateway, verifies the custom HS256
 * JWT injected by the VTL mapping template, batch-inserts all packets into the
 * udp_messages table, and returns a Replies array so Proxylity can ACK each sender.
 *
 * Environment variables (set via `supabase secrets set`):
 *   JWT_SECRET       — HS256 signing secret shared with the JwtSigner Lambda
 *   SERVICE_ROLE_KEY — set via `supabase secrets set SERVICE_ROLE_KEY=...` (bypasses RLS)
 *   SUPABASE_URL     — auto-injected by the Supabase runtime (do not set manually)
 *
 * Extending this function:
 *   - Parse packet payloads to extract typed fields (temperature, device ID, etc.)
 *   - Broadcast to a Supabase Realtime channel for live dashboards
 *   - Call other Edge Functions or external APIs
 *   - Apply per-device logic based on source IP or payload content
 *   The contract with Proxylity is only the Request/Response shape in _shared/proxylity.ts.
 */

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { parseBatch, decodeData } from "../_shared/proxylity.ts";

// ---------------------------------------------------------------------------
// JWT verification (Web Crypto API — no external dependencies)
// ---------------------------------------------------------------------------

async function verifyJwt(token: string, secret: string): Promise<boolean> {
  const parts = token.split(".");
  if (parts.length !== 3) return false;
  const [header, payload, signature] = parts;

  // Verify the HMAC-SHA256 signature
  const signingInput = new TextEncoder().encode(`${header}.${payload}`);
  const sigBytes = Uint8Array.from(
    atob(signature.replace(/-/g, "+").replace(/_/g, "/")),
    (c) => c.charCodeAt(0),
  );
  const cryptoKey = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["verify"],
  );
  const valid = await crypto.subtle.verify("HMAC", cryptoKey, sigBytes, signingInput);
  if (!valid) return false;

  // Verify the expiry claim
  try {
    const claims = JSON.parse(
      atob(payload.replace(/-/g, "+").replace(/_/g, "/")),
    );
    return typeof claims.exp === "number" &&
      claims.exp > Math.floor(Date.now() / 1000);
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,        // auto-injected by the Supabase runtime
  Deno.env.get("SERVICE_ROLE_KEY")!,    // set via: supabase secrets set SERVICE_ROLE_KEY=...
);

const JWT_SECRET = Deno.env.get("JWT_SECRET")!;

Deno.serve(async (req: Request) => {
  // Verify the custom JWT injected by API Gateway's VTL template.
  // Supabase's own JWT gateway check is disabled for this function (verify_jwt = false
  // in config.toml) because we are using a custom token, not a Supabase Auth JWT.
  const authHeader = req.headers.get("Authorization") ?? "";
  const token = authHeader.startsWith("Bearer ") ? authHeader.slice(7) : "";
  if (!token || !(await verifyJwt(token, JWT_SECRET))) {
    return new Response(JSON.stringify({ error: "Unauthorized" }), {
      status: 401,
      headers: { "Content-Type": "application/json" },
    });
  }

  const rawBody = await req.text();
  console.log("[udp-receiver] raw body:", rawBody);
  const { messages, reply, build } = await parseBatch(req, rawBody);

  // Map each RequestPacket to a row.
  // Extend this mapping for your own wire protocol — parse the payload string
  // into typed fields, validate ranges, apply calibration, etc.
  const rows = messages.map((msg) => ({
    tag: msg.Tag,
    received_at: msg.ReceivedAt,
    source_ip: msg.Remote.IpAddress,
    source_port: msg.Remote.Port,
    payload: decodeData(msg),
  }));

  // Single batch insert — one Postgres round-trip for all packets in the batch.
  const { error } = await supabase.from("udp_messages").insert(rows);

  // Build the Replies array.  Each Tag gets an OK or ERR response so the
  // sending device receives an ACK over UDP.  For pure fire-and-forget devices
  // that never read replies, you can return { Replies: [] } instead.
  for (const msg of messages) {
    reply(msg.Tag, error ? `ERR\n${error.message}` : "OK");
  }

  return Response.json(build());
});
