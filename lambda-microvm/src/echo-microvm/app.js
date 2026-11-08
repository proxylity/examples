// Proof-of-concept application that runs inside a Lambda MicroVM.
//
// Proxylity delivers batches of UDP packets to the MicroVM endpoint as an HTTPS POST on port 8080:
//
//   { "Messages": [ { "Tag": "...", "Formatter": "base64", "Data": "<base64>", "Remote": {...}, "Local": {...} } ] }
//
// and expects a JSON body in response. Each entry in "Replies" is sent back to the
// original sender as a UDP packet:
//
//   { "Replies": [ { "Tag": "<tag from the message>", "Data": "<base64>" } ] }
//
// When Proxylity starts a MicroVM, Lambda calls this app's /run lifecycle hook on port 9000 with the
// MicroVM's ID and the run hook payload Proxylity supplies (destination, tenant, formatter, and the
// destination's RunHookExtraData). Lambda only routes packet traffic to the MicroVM after /run returns.
// While building the image, Lambda calls /ready (also on port 9000) and snapshots the app once it returns 200.
//
// This app logs every packet it receives (forwarded to CloudWatch Logs by the MicroVM's execution
// role) and replies with a short text message. Because a MicroVM keeps its memory between requests,
// the running packet count in the reply shows that the same MicroVM is serving the sender.

const http = require('http');

const APP_PORT = 8080;   // packet batches from Proxylity
const HOOK_PORT = 9000;  // Lambda lifecycle hooks; not reachable with Proxylity's port-8080 token
const RUN_HOOK_PATH = '/aws/lambda-microvms/runtime/v1/run';
const READY_HOOK_PATH = '/aws/lambda-microvms/runtime/v1/ready';

// Lambda calls /ready while building the image and snapshots the app once it returns 200. Report ready only when
// both servers are listening, so every MicroVM starts from a snapshot that can accept packets immediately.
let listening = 0;

// Set by the /run hook. Lambda snapshots the app after startup, so per-MicroVM values must come
// from /run rather than being generated at startup (they'd be identical in every MicroVM).
let microvmId = 'unknown';
let runConfig = {};
let totalPackets = 0;

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

// Lambda posts { "microvmId": "...", "runHookPayload": "<string>" }. Proxylity's payload is a
// JSON string: { Version, DestinationName, ImageArn, TenantId, Region, Formatter, Port, StartedAt, ExtraData }.
function handleRun(body) {
  const request = JSON.parse(body || '{}');
  microvmId = request.microvmId || microvmId;
  try {
    runConfig = request.runHookPayload ? JSON.parse(request.runHookPayload) : {};
  } catch {
    runConfig = {};
    console.error('Run hook payload was not JSON; continuing without it.');
  }
  console.log(JSON.stringify({ event: 'run', microvm: microvmId, config: runConfig }));
}

function handleBatch(body) {
  const messages = Array.isArray(body && body.Messages) ? body.Messages : [];
  const replies = [];
  const tenant = runConfig.TenantId ? ` for tenant ${runConfig.TenantId}` : '';

  for (const message of messages) {
    totalPackets++;
    const payload = Buffer.from(message.Data || '', 'base64');
    const from = message.Remote ? `${message.Remote.IpAddress}:${message.Remote.Port}` : 'unknown';

    console.log(JSON.stringify({
      event: 'packet',
      microvm: microvmId,
      count: totalPackets,
      from,
      bytes: payload.length,
      text: payload.toString('utf8').slice(0, 200),
    }));

    const reply = `Hello from MicroVM ${microvmId}${tenant}. Packet #${totalPackets}, ${payload.length} byte(s) received.`;
    replies.push({ Tag: message.Tag, Data: Buffer.from(reply, 'utf8').toString('base64') });
  }

  return { Replies: replies };
}

const app = http.createServer(async (req, res) => {
  const body = await readBody(req);
  if (req.method !== 'POST') {
    // Anything other than a Proxylity batch (e.g. a manual GET with a token) gets a status document.
    sendJson(res, 200, { status: 'ok', microvm: microvmId, totalPackets, config: runConfig });
    return;
  }
  try {
    sendJson(res, 200, handleBatch(JSON.parse(body || '{}')));
  } catch (err) {
    console.error(`Unable to process request: ${err.message}`);
    sendJson(res, 400, { error: 'invalid request' });
  }
});

const hooks = http.createServer(async (req, res) => {
  const body = await readBody(req);
  if (req.method === 'POST' && req.url === READY_HOOK_PATH) {
    // 503 tells Lambda to retry until ReadyTimeoutInSeconds; respond immediately rather than holding the request.
    sendJson(res, listening === 2 ? 200 : 503, {});
    return;
  }
  if (req.method === 'POST' && req.url === RUN_HOOK_PATH) {
    try {
      handleRun(body);
      sendJson(res, 200, {});
    } catch (err) {
      // Returning an error fails the run, so Lambda doesn't route traffic to a misconfigured MicroVM.
      console.error(`Run hook failed: ${err.message}`);
      sendJson(res, 500, { error: 'run hook failed' });
    }
    return;
  }
  // Other lifecycle hooks aren't enabled for this image; acknowledge anything else.
  sendJson(res, 200, {});
});

app.listen(APP_PORT, () => { listening++; console.log(`Listening for packet batches on port ${APP_PORT}`); });
hooks.listen(HOOK_PORT, () => { listening++; console.log(`Listening for lifecycle hooks on port ${HOOK_PORT}`); });
