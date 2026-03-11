'use strict';

/**
 * JwtSigner Lambda
 *
 * Called exclusively by the Step Functions JWT refresh loop — never on the
 * hot UDP data path. Signs a short-lived HS256 JWT using a secret stored in
 * SSM Parameter Store and returns it alongside the calculated wait duration.
 *
 * The Step Functions state machine uses the returned waitSeconds to schedule
 * the next refresh, ensuring a fresh token is always in the API Gateway stage
 * variable before the current one expires.
 *
 * No external npm dependencies — uses the AWS SDK v3 bundled in the Node.js
 * 20.x Lambda runtime and the Node.js built-in crypto module.
 *
 * Environment variables:
 *   JWT_SECRET_PARAM            — SSM SecureString path, e.g. /supabase-udp/jwt-secret
 *   TOKEN_TTL_SECONDS           — JWT lifetime (default 3600)
 *   TOKEN_REFRESH_BUFFER_SECONDS — Seconds before expiry to refresh (default 300)
 */

const { SSMClient, GetParameterCommand } = require('@aws-sdk/client-ssm');
const { createHmac } = require('crypto');

const ssm = new SSMClient({});
const TTL    = parseInt(process.env.TOKEN_TTL_SECONDS            ?? '3600', 10);
const BUFFER = parseInt(process.env.TOKEN_REFRESH_BUFFER_SECONDS ?? '300',  10);
const PARAM  = process.env.JWT_SECRET_PARAM;

// ---------------------------------------------------------------------------
// HS256 JWT signing (RFC 7519) — no external libraries
// ---------------------------------------------------------------------------

function base64url(buf) {
  return buf.toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=/g,  '');
}

function signJwt(secret, payload) {
  const header  = base64url(Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })));
  const body    = base64url(Buffer.from(JSON.stringify(payload)));
  const signing = `${header}.${body}`;
  const sig     = base64url(createHmac('sha256', secret).update(signing).digest());
  return `${header}.${body}.${sig}`;
}

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

exports.handler = async () => {
  const { Parameter } = await ssm.send(
    new GetParameterCommand({ Name: PARAM, WithDecryption: true }),
  );

  const now   = Math.floor(Date.now() / 1000);
  const token = signJwt(Parameter.Value, {
    iss: 'proxylity-supabase-udp',
    iat: now,
    exp: now + TTL,
  });

  return {
    token,
    expiresIn:   TTL,
    // The state machine's Wait state uses this value. Refreshing early
    // (TTL - BUFFER seconds) ensures the stage variable is updated before
    // the current token expires, even with clock skew or slow propagation.
    waitSeconds: TTL - BUFFER,
  };
};
