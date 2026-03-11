/**
 * Proxylity UDP Gateway — shared types and helpers for Supabase Edge Functions.
 *
 * This is the only Proxylity-aware file in the Supabase project. Import it into
 * any Edge Function that receives UDP batches from Proxylity via API Gateway.
 *
 * CONTRACT
 * --------
 * Proxylity delivers a POST body with the shape:  { "Messages": RequestPacket[] }
 * The Edge Function must return:                  { "Replies":  ResponsePacket[] }
 *
 * Every RequestPacket.Data is base64-encoded regardless of the Formatter setting
 * on the Proxylity Destination. Use decodeData() to get the UTF-8 string payload.
 *
 * Replies are optional. Omit a Tag from Replies to send no response to that device
 * (normal for fire-and-forget IoT telemetry). Include a Tag to echo data back.
 *
 * Reference: https://proxylity.com/docs/destinations/json-packet-format.html
 */

export interface RequestPacket {
  /** Unique identifier for this packet within the batch. Include in Replies to respond to the sender. */
  Tag: string;
  /** The UDP sender's address. */
  Remote: { IpAddress: string; Port: number };
  /** The Proxylity listener endpoint that received the packet. */
  Local: { Domain: string; Port: number };
  /** ISO-8601 timestamp of when the packet arrived at the Proxylity ingress. */
  ReceivedAt: string;
  /** Encoding used for Data. Matches the Formatter configured on the Proxylity Destination. */
  Formatter?: "base64" | "hex" | "utf8" | "ascii";
  /** Packet payload, encoded per Formatter. Use decodeData() to get the raw string. */
  Data: string;
}

export interface ResponsePacket {
  /** Must match the Tag of the RequestPacket to route the reply back to that sender. */
  Tag: string;
  /** Base64-encoded reply payload. Use encodeReply() to build from a plain string. */
  Data: string;
}

export interface ProxylityRequest {
  Messages: RequestPacket[];
}

export interface ProxylityResponse {
  Replies: ResponsePacket[];
}

/**
 * Decode the Data field of a RequestPacket to a UTF-8 string.
 * Handles utf8, ascii (passthrough) and base64/base64url (atob) formatters.
 */
export function decodeData(packet: RequestPacket): string {
  if (packet.Formatter === "utf8" || packet.Formatter === "ascii") {
    return packet.Data;
  }
  // base64 or base64url → standard base64 before atob
  const b64 = packet.Data.replace(/-/g, "+").replace(/_/g, "/");
  return atob(b64);
}

/**
 * Build a ResponsePacket from a plain-text reply string.
 * The tag must match the RequestPacket.Tag you are responding to.
 * Data is encoded to match the Formatter of the originating request packet.
 */
export function encodeReply(tag: string, text: string, formatter?: RequestPacket["Formatter"]): ResponsePacket {
  const data = (formatter === "utf8" || formatter === "ascii") ? text : btoa(text);
  return { Tag: tag, Data: data };
}

/**
 * Parse the Proxylity batch from an incoming Request, and return a helper
 * that builds the ProxylityResponse as you process each message.
 */
export async function parseBatch(req: Request, rawBody?: string): Promise<{
  messages: RequestPacket[];
  reply: (tag: string, text: string) => void;
  build: () => ProxylityResponse;
}> {
  const body: ProxylityRequest = rawBody
    ? JSON.parse(rawBody)
    : await req.json();
  const replies: ResponsePacket[] = [];
  return {
    messages: body.Messages,
    reply: (tag, text) => {
      const msg = body.Messages.find((m) => m.Tag === tag);
      replies.push(encodeReply(tag, text, msg?.Formatter));
    },
    build: () => ({ Replies: replies }),
  };
}
