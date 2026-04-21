using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<UiHandler.JsonContext>))]

namespace UiHandler;

public class Function
{
    private static readonly string RealtimeHost =
        Environment.GetEnvironmentVariable("APPSYNC_REALTIME_HOST")
        ?? throw new InvalidOperationException("APPSYNC_REALTIME_HOST environment variable is not set.");

    private static readonly string HttpHost =
        Environment.GetEnvironmentVariable("APPSYNC_HTTP_HOST")
        ?? throw new InvalidOperationException("APPSYNC_HTTP_HOST environment variable is not set.");

    private static readonly string ApiKey =
        Environment.GetEnvironmentVariable("APPSYNC_API_KEY")
        ?? throw new InvalidOperationException("APPSYNC_API_KEY environment variable is not set.");

    private static readonly string Channel =
        Environment.GetEnvironmentVariable("APPSYNC_CHANNEL")
        ?? throw new InvalidOperationException("APPSYNC_CHANNEL environment variable is not set.");

    public APIGatewayProxyResponse FunctionHandler(APIGatewayProxyRequest _, ILambdaContext context)
    {
        // Serialize config as JSON so we can inject it safely into a <script> block
        // without any risk of value content breaking out of the JS string context.
        var configJson = JsonSerializer.Serialize(
            new UiConfig { RealtimeHost = RealtimeHost, HttpHost = HttpHost, ApiKey = ApiKey, Channel = Channel },
            JsonContext.Default.UiConfig);

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/html; charset=utf-8",
                ["Cache-Control"] = "no-store"
            },
            Body = BuildHtml(configJson)
        };
    }

    // $$""" means {{ and }} are literal { and } — only {{configJson}} is an interpolation site.
    private static string BuildHtml(string configJson) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Packet Capture</title>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

            html, body { height: 100%; overflow: hidden; }

            body {
              display: flex;
              flex-direction: column;
              font-family: ui-monospace, 'Cascadia Code', 'Courier New', monospace;
              background: #0d1117;
              color: #c9d1d9;
            }

            /* ── Header bar ── */
            #hdr {
              display: flex;
              align-items: center;
              gap: 1rem;
              padding: 0.4rem 1rem;
              background: #161b22;
              border-bottom: 1px solid #30363d;
              flex-shrink: 0;
            }
            #hdr h1   { font-size: 0.9rem; color: #58a6ff; font-weight: 600; }
            #status   { font-size: 0.72rem; color: #8b949e; margin-left: auto; }

            /* ── Packet list pane ── */
            #list-pane {
              flex: 0 0 40%;
              overflow-y: auto;
              border-bottom: 3px solid #30363d;
            }
            #pkt-table { width: 100%; border-collapse: collapse; font-size: 0.72rem; }
            #pkt-table th {
              text-align: left;
              padding: 0.28rem 0.6rem;
              background: #161b22;
              color: #8b949e;
              border-bottom: 2px solid #30363d;
              position: sticky; top: 0; z-index: 1;
              text-transform: uppercase;
              font-size: 0.62rem;
              letter-spacing: 0.06em;
              font-weight: 500;
            }
            #pkt-table td { padding: 0.26rem 0.6rem; border-bottom: 1px solid #21262d; }
            #pkt-table tbody tr { cursor: pointer; }
            #pkt-table tbody tr:hover td  { background: #161b22; }
            #pkt-table tbody tr.sel    td { background: #1c2d3f; color: #79c0ff; }

            .proto     { font-weight: 700; }
            .proto-wg  { color: #d2a8ff; }
            .proto-udp { color: #56d364; }
            .proto-tcp { color: #ffa657; }

            /* ── Detail pane ── */
            #detail-pane {
              flex: 1;
              overflow-y: auto;
              background: #fff;
              color: #24292f;
              font-family: 'Segoe UI', system-ui, sans-serif;
              font-size: 13px;
            }

            #detail-hint {
              padding: 2rem;
              color: #8b949e;
              font-style: italic;
            }

            /* Wireshark-style layer tree */
            .layer-tree details {
              border-bottom: 1px solid #d8dee4;
            }
            .layer-tree > details > summary {
              display: flex;
              align-items: center;
              gap: 6px;
              padding: 5px 10px;
              background: #f6f8fa;
              border-bottom: 1px solid #d8dee4;
              cursor: pointer;
              list-style: none;
              user-select: none;
              font-weight: 500;
              font-size: 12px;
            }
            .layer-tree > details > summary::-webkit-details-marker { display: none; }
            .layer-tree > details > summary:hover { background: #edf2f7; }

            .arrow {
              display: inline-block;
              width: 12px;
              font-size: 9px;
              color: #6e7781;
              transition: transform 0.12s;
            }
            details[open] > summary .arrow { transform: rotate(90deg); }

            .lname { color: #0969da; font-weight: 700; min-width: 48px; }
            .lsumm { color: #24292f; }

            /* Field rows */
            .flist {
              padding: 3px 0 3px 28px;
              border-left: 2px solid #d8dee4;
              margin-left: 16px;
            }
            .frow {
              display: flex;
              gap: 8px;
              padding: 2px 4px;
              font-size: 12px;
              font-family: 'Cascadia Code', 'Courier New', monospace;
            }
            .frow:hover { background: #f6f8fa; }
            .flbl { color: #57606a; min-width: 160px; flex-shrink: 0; }
            .fval { color: #24292f; }

            /* Sub-field nested <details> inside a field list */
            .flist details { border: none; }
            .flist details > summary {
              display: flex;
              align-items: center;
              gap: 6px;
              list-style: none;
              padding: 2px 4px;
              background: none;
              cursor: pointer;
              font-family: 'Cascadia Code', 'Courier New', monospace;
              font-size: 12px;
              user-select: none;
            }
            .flist details > summary::-webkit-details-marker { display: none; }
            .flist details > summary:hover { background: #f6f8fa; }
            .flist .flist { margin-left: 16px; border-left-color: #eaeef2; }

            /* New-row arrival pulse */
            @keyframes row-arrive {
              0%   { background: #0d3320; }
              100% { background: transparent; }
            }
            .arriving td { animation: row-arrive 1s ease-out; }

            /* Hex dump block */
            .hexdump {
              font-family: 'Cascadia Code', 'Courier New', monospace;
              font-size: 11px;
              white-space: pre;
              background: #f6f8fa;
              border: 1px solid #d8dee4;
              padding: 6px 10px;
              margin: 4px 0 4px 28px;
              overflow-x: auto;
              max-height: 160px;
              overflow-y: auto;
              color: #24292f;
            }
          </style>
        </head>
        <body>
          <div id="hdr">
            <h1>&#x1F4E1; Live Packet Capture</h1>
            <span id="status">Connecting…</span>
          </div>

          <div id="list-pane">
            <table id="pkt-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Time (UTC)</th>
                  <th>Proto</th>
                  <th>Source</th>
                  <th>Bytes</th>
                </tr>
              </thead>
              <tbody id="pkt-tbody"></tbody>
            </table>
          </div>

          <div id="detail-pane">
            <p id="detail-hint">&#x2190; Click a packet to inspect its layers</p>
            <div id="detail-tree"></div>
          </div>

          <script>
            const CONFIG  = {{configJson}};

            const statusEl = document.getElementById('status');
            const tbody    = document.getElementById('pkt-tbody');
            const hint     = document.getElementById('detail-hint');
            const tree     = document.getElementById('detail-tree');

            const packets  = [];
            let   counter  = 0;
            let   selectedTr = null;

            // ── WebSocket ──────────────────────────────────────────────────────
            function amzDate() {
              return new Date().toISOString().replace(/[:\-]|\.\d{3}/g, '');
            }

            function toBase64Url(str) {
              return btoa(str).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
            }

            function makeAuth() {
              return { host: CONFIG.httpHost, 'x-amz-date': amzDate(), 'x-api-key': CONFIG.apiKey };
            }

            function connect() {
              const auth         = makeAuth();
              const encodedAuth  = toBase64Url(JSON.stringify(auth));
              const ws = new WebSocket(
                'wss://' + CONFIG.realtimeHost + '/event/realtime',
                ['aws-appsync-event-ws', 'header-' + encodedAuth]
              );

              ws.onopen = () => {
                statusEl.textContent = 'Connected — initialising…';
                ws.send(JSON.stringify({ type: 'connection_init' }));
              };

              ws.onmessage = (e) => {
                const msg = JSON.parse(e.data);
                if (msg.type === 'ka') return;
                if (msg.type === 'connection_ack') {
                  const subAuth = makeAuth();
                  ws.send(JSON.stringify({
                    type:    'subscribe',
                    id:      crypto.randomUUID(),
                    channel: CONFIG.channel,
                    authorization: subAuth,
                    payload: {
                      channel: CONFIG.channel,
                      extensions: { authorization: subAuth }
                    }
                  }));
                  return;
                }
                if (msg.type === 'subscribe_success') {
                  statusEl.textContent = 'Subscribed — packets will appear as they arrive.';
                  return;
                }
                if (msg.type === 'data' && msg.event) {
                  addPacket(JSON.parse(msg.event));
                }
              };

              ws.onerror = () => { statusEl.textContent = 'WebSocket error — see console.'; };
              ws.onclose = () => {
                statusEl.textContent = 'Disconnected — reconnecting in 5 s…';
                setTimeout(connect, 5000);
              };
            }

            // ── Packet list ────────────────────────────────────────────────────
            function addPacket(pkt) {
              const idx   = packets.length;
              packets.push(pkt);
              counter++;

              const ts    = pkt.capturedAt
                ? pkt.capturedAt.replace('T', ' ').substring(0, 23) + ' Z' : '—';
              const src   = (pkt.sourceIp ?? '—') + ':' + (pkt.sourcePort ?? 0);
              const proto = (pkt.protocol ?? 'udp').toLowerCase();

              const tr = document.createElement('tr');
              tr.dataset.idx = idx;
              tr.innerHTML =
                '<td>' + counter + '</td>' +
                '<td>' + esc(ts) + '</td>' +
                '<td><span class="proto proto-' + esc(proto) + '">' + esc(proto.toUpperCase()) + '</span></td>' +
                '<td>' + esc(src) + '</td>' +
                '<td>' + (pkt.lengthBytes ?? '—') + '</td>';

              tr.addEventListener('click', () => select(idx, tr));
              tbody.prepend(tr);
              tr.classList.add('arriving');
              setTimeout(() => tr.classList.remove('arriving'), 1000);

              if (tbody.rows.length > 500) tbody.deleteRow(tbody.rows.length - 1);
            }

            function select(idx, tr) {
              if (selectedTr) selectedTr.classList.remove('sel');
              tr.classList.add('sel');
              selectedTr = tr;
              renderDetail(packets[idx]);
            }

            document.addEventListener('keydown', (e) => {
              if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
              e.preventDefault();
              const rows = Array.from(tbody.rows);
              if (rows.length === 0) return;
              let pos = selectedTr ? rows.indexOf(selectedTr) : -1;
              if (pos === -1) {
                pos = 0;
              } else if (e.key === 'ArrowDown') {
                pos = Math.min(pos + 1, rows.length - 1);
              } else {
                pos = Math.max(pos - 1, 0);
              }
              const next = rows[pos];
              select(parseInt(next.dataset.idx), next);
              next.scrollIntoView({ block: 'nearest' });
            });

            // ── Detail panel ───────────────────────────────────────────────────
            function renderDetail(pkt) {
              hint.hidden = true;
              tree.innerHTML = '';

              if (!pkt.layers || pkt.layers.length === 0) {
                tree.textContent = 'No layer data available.';
                return;
              }

              const wrap = document.createElement('div');
              wrap.className = 'layer-tree';

              pkt.layers.forEach(layer => {
                const det = document.createElement('details');
                det.open  = true;

                const sum = document.createElement('summary');
                sum.innerHTML =
                  '<span class="arrow">&#x25BA;</span>' +
                  '<span class="lname">' + esc(layer.name    ?? '') + '</span>' +
                  '<span class="lsumm">' + esc(layer.summary ?? '') + '</span>';
                det.appendChild(sum);

                const fl = document.createElement('div');
                fl.className = 'flist';
                (layer.fields ?? []).forEach(f => renderField(f, fl));
                det.appendChild(fl);
                wrap.appendChild(det);
              });

              tree.appendChild(wrap);
            }

            function renderField(field, container) {
              if (field.label === 'Hex Dump') {
                const pre = document.createElement('pre');
                pre.className = 'hexdump';
                pre.textContent = field.value;
                container.appendChild(pre);
                return;
              }

              if (field.subFields && field.subFields.length > 0) {
                const det = document.createElement('details');
                const sum = document.createElement('summary');
                sum.innerHTML =
                  '<span class="arrow">&#x25BA;</span>' +
                  '<span class="flbl">' + esc(field.label) + '</span>' +
                  '<span class="fval">' + esc(field.value) + '</span>';
                det.appendChild(sum);

                const sub = document.createElement('div');
                sub.className = 'flist';
                field.subFields.forEach(sf => renderField(sf, sub));
                det.appendChild(sub);
                container.appendChild(det);
                return;
              }

              const row = document.createElement('div');
              row.className = 'field-row frow';
              row.innerHTML =
                '<span class="flbl">' + esc(field.label) + '</span>' +
                '<span class="fval">' + esc(field.value) + '</span>';
              container.appendChild(row);
            }

            function esc(s) {
              return String(s)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;');
            }

            connect();
          </script>
        </body>
        </html>
        """;
}

public class UiConfig
{
    [JsonPropertyName("realtimeHost")]
    public string RealtimeHost { get; set; } = string.Empty;

    [JsonPropertyName("httpHost")]
    public string HttpHost { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;
}

[JsonSerializable(typeof(APIGatewayProxyRequest))]
[JsonSerializable(typeof(APIGatewayProxyResponse))]
[JsonSerializable(typeof(UiConfig))]
internal partial class JsonContext : JsonSerializerContext;
