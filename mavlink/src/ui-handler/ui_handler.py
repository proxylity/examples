"""
ui_handler.py -- Lambda function that serves the drone fleet tracker map UI.

Served via Lambda URL (AuthType NONE). On each request the handler:
1. Fetches current drone positions from AWS Location Service.
2. Injects the positions plus AppSync Events connection details into the HTML.
3. Returns the page -- the browser connects to AppSync Events over WebSocket
   and receives real-time position updates with zero configuration.
"""

import json
import os
import logging

import boto3

logger = logging.getLogger()
logger.setLevel(logging.INFO)

# ---- Environment ----
APPSYNC_REALTIME_HOST = os.environ.get("APPSYNC_REALTIME_HOST", "")
APPSYNC_HTTP_HOST = os.environ.get("APPSYNC_HTTP_HOST", "")
APPSYNC_API_KEY = os.environ.get("APPSYNC_API_KEY", "")
APPSYNC_CHANNEL = os.environ.get("APPSYNC_CHANNEL", "/drones/positions")
TRACKER_NAME = os.environ.get("TRACKER_NAME", "")
REGION = os.environ.get("AWS_REGION_NAME", os.environ.get("AWS_REGION", "us-west-2"))

location_client = boto3.client("location", region_name=REGION)


def _fetch_initial_positions():
    """Call ListDevicePositions and return a JSON-serialisable list."""
    positions = []
    try:
        paginator = location_client.get_paginator("list_device_positions")
        for page in paginator.paginate(TrackerName=TRACKER_NAME):
            for entry in page.get("Entries", []):
                pos = entry.get("Position", [0, 0])  # [lon, lat]
                props = entry.get("PositionProperties", {})
                alt_raw = props.get("alt_mm")
                vel_raw = props.get("vel_cms")
                sats_raw = props.get("satellites")
                sample_time = entry.get("SampleTime")
                positions.append({
                    "deviceId": entry.get("DeviceId", ""),
                    "lat": pos[1] if len(pos) > 1 else 0,
                    "lon": pos[0] if len(pos) > 0 else 0,
                    "alt": float(alt_raw) / 1000.0 if alt_raw is not None else None,
                    "vel": float(vel_raw) / 100.0 if vel_raw is not None else None,
                    "sats": int(sats_raw) if sats_raw is not None else None,
                    "ts": sample_time.isoformat() if sample_time else None,
                })
    except Exception:
        logger.exception("Failed to fetch initial positions from tracker %s", TRACKER_NAME)
    return positions


def handler(event, context):
    """Lambda URL handler -- returns the map HTML."""
    initial = _fetch_initial_positions()

    html = HTML_TEMPLATE.replace("__INITIAL_POSITIONS_PLACEHOLDER__",
                                 json.dumps(initial, default=str))
    html = html.replace("__APPSYNC_REALTIME_HOST__", APPSYNC_REALTIME_HOST)
    html = html.replace("__APPSYNC_HTTP_HOST__", APPSYNC_HTTP_HOST)
    html = html.replace("__APPSYNC_API_KEY__", APPSYNC_API_KEY)
    html = html.replace("__APPSYNC_CHANNEL__", APPSYNC_CHANNEL)

    return {
        "statusCode": 200,
        "headers": {"content-type": "text/html; charset=utf-8"},
        "body": html,
    }


# ---------------------------------------------------------------------------
# HTML template -- everything below is the self-contained map dashboard.
# Placeholders are replaced at render time by the handler above.
# ---------------------------------------------------------------------------

HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Drone Fleet Tracker -- Proxylity UDP Gateway</title>
<link rel="stylesheet" href="https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.css">
<script src="https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.js"></script>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{
  --bg-dark:#0f0f1a;--bg-panel:#1a1a2e;--bg-input:#16213e;
  --border:#2a2a4a;--text-primary:#e0e0f0;--text-secondary:#8888aa;
  --accent:#00d4ff;--danger:#ff4466;--success:#00e676;--warning:#ffab00;
  --font:'Segoe UI',-apple-system,BlinkMacSystemFont,sans-serif;
  --mono:'Cascadia Code','Fira Code','Consolas',monospace;
}
html,body{height:100%;font-family:var(--font);background:var(--bg-dark);color:var(--text-primary);overflow:hidden}
#app{display:flex;flex-direction:column;height:100vh}

/* -- Header -- */
#header{display:flex;align-items:center;gap:10px;padding:10px 16px;background:var(--bg-panel);border-bottom:1px solid var(--border);z-index:10}
#header .logo{font-size:20px}
#header .title{font-size:14px;font-weight:600;color:var(--accent);letter-spacing:.5px;white-space:nowrap}
#header .subtitle{font-size:10px;color:var(--text-secondary);white-space:nowrap}

/* -- Map -- */
#map-container{flex:1;position:relative}
#map{width:100%;height:100%}

/* -- Status bar -- */
#status-bar{display:flex;align-items:center;justify-content:space-between;padding:6px 16px;background:var(--bg-panel);border-top:1px solid var(--border);font-size:12px;z-index:10}
#status-message{display:flex;align-items:center;gap:8px}
#status-indicator{width:8px;height:8px;border-radius:50%;background:var(--text-secondary);transition:background .3s}
#status-indicator.active{background:var(--success);box-shadow:0 0 6px var(--success);animation:pulse 2s ease-in-out infinite}
#status-indicator.error{background:var(--danger);box-shadow:0 0 6px var(--danger)}
#status-indicator.warning{background:var(--warning);box-shadow:0 0 6px var(--warning)}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}
#status-text{color:var(--text-secondary)}
#status-right{color:var(--text-secondary);font-family:var(--mono);font-size:11px}

/* -- Drone markers -- */

/* -- Legend -- */
#legend{position:absolute;bottom:12px;right:12px;background:rgba(26,26,46,.92);border:1px solid var(--border);border-radius:8px;padding:10px 14px;font-size:11px;z-index:5;display:none;max-height:200px;overflow-y:auto}
.legend-title{font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:var(--text-secondary);margin-bottom:6px}
.legend-item{display:flex;align-items:center;gap:6px;padding:2px 0}
.legend-dot{width:8px;height:8px;border-radius:50%;flex-shrink:0}
.legend-name{color:var(--text-primary);font-family:var(--mono);font-size:10px}

/* -- MapLibre popup overrides -- */
.maplibregl-popup-content{background:var(--bg-panel)!important;color:var(--text-primary)!important;border:1px solid var(--border)!important;border-radius:8px!important;padding:0!important;box-shadow:0 4px 24px rgba(0,0,0,.5)!important;min-width:220px}
.maplibregl-popup-tip{border-top-color:var(--bg-panel)!important}
.maplibregl-popup-close-button{color:var(--text-secondary)!important;font-size:18px!important;right:6px!important;top:4px!important}
.maplibregl-popup-close-button:hover{color:var(--text-primary)!important;background:transparent!important}
.drone-popup{padding:12px 14px}
.drone-popup .popup-header{display:flex;align-items:center;gap:8px;margin-bottom:10px;padding-bottom:8px;border-bottom:1px solid var(--border)}
.drone-popup .popup-header .dot{width:10px;height:10px;border-radius:50%;flex-shrink:0}
.drone-popup .popup-header .device-name{font-weight:600;font-size:14px;color:var(--accent)}
.drone-popup .popup-row{display:flex;justify-content:space-between;padding:3px 0;font-size:12px}
.drone-popup .popup-label{color:var(--text-secondary)}
.drone-popup .popup-value{font-family:var(--mono);font-size:11px;color:var(--text-primary)}

/* -- Attribution -- */
.maplibregl-ctrl-attrib{background:rgba(15,15,26,.7)!important;color:var(--text-secondary)!important;font-size:10px!important}
.maplibregl-ctrl-attrib a{color:var(--accent)!important}
</style>
</head>
<body>
<div id="app">
  <div id="header">
    <span class="logo">&#x1F6E9;&#xFE0F;</span>
    <div>
      <div class="title">Drone Fleet Tracker</div>
      <div class="subtitle">Proxylity UDP Gateway + AppSync Events</div>
    </div>
  </div>
  <div id="map-container">
    <div id="map"></div>
    <div id="legend"><div class="legend-title">Fleet</div><div id="legend-items"></div></div>
  </div>
  <div id="status-bar">
    <div id="status-message"><div id="status-indicator"></div><span id="status-text">Initializing...</span></div>
    <div id="status-right"></div>
  </div>
</div>
<script>
(function() {
  'use strict';

  // ---- Server-injected config ----
  var INITIAL_POSITIONS = __INITIAL_POSITIONS_PLACEHOLDER__;
  var REALTIME_HOST = '__APPSYNC_REALTIME_HOST__';
  var HTTP_HOST     = '__APPSYNC_HTTP_HOST__';
  var API_KEY       = '__APPSYNC_API_KEY__';
  var CHANNEL       = '__APPSYNC_CHANNEL__';

  // ---- Constants ----
  var COLORS = ['#00d4ff','#ff6b6b','#51cf66','#ffd43b','#cc5de8','#ff922b','#22b8cf','#f06595'];
  var MAX_TRAIL = 200;
  var RECONNECT_MS = 3000;

  // ---- State ----
  var map = null;
  var droneState = {};
  var droneIndex = 0;
  var currentPopup = null;
  var ws = null;
  var reconnectTimer = null;

  // ---- DOM refs ----
  var statusIndicator = document.getElementById('status-indicator');
  var statusText      = document.getElementById('status-text');
  var statusRight     = document.getElementById('status-right');
  var legendEl        = document.getElementById('legend');
  var legendItems     = document.getElementById('legend-items');

  // ---- Helpers ----
  function setStatus(text, level) {
    statusText.textContent = text;
    statusIndicator.className = '';
    if (level) statusIndicator.classList.add(level);
  }
  function fmtTime(d) {
    return String(d.getHours()).padStart(2,'0') + ':' +
           String(d.getMinutes()).padStart(2,'0') + ':' +
           String(d.getSeconds()).padStart(2,'0');
  }
  function escapeHtml(s) { var d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

  // ---- Initialize map ----
  if (typeof maplibregl === 'undefined') {
    document.getElementById('map').innerHTML =
      '<div style="display:flex;align-items:center;justify-content:center;height:100%;color:#8888aa;font-size:16px;padding:40px;text-align:center">'
      + 'MapLibre GL JS could not load.<br>Open this page directly in a browser.'
      + '</div>';
    return;
  }

  map = new maplibregl.Map({
    container: 'map',
    style: {
      version: 8,
      sources: { osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>' } },
      layers: [{ id: 'osm', type: 'raster', source: 'osm', minzoom: 0, maxzoom: 19 }],
      glyphs: 'https://demotiles.maplibre.org/font/{fontstack}/{range}.pbf'
    },
    center: [-118.8434, 45.6951],
    zoom: 13
  });

  map.on('load', function() {
    // Dark overlay
    map.addSource('dark-overlay', { type: 'geojson', data: { type: 'Feature', geometry: { type: 'Polygon',
      coordinates: [[[-180,-90],[180,-90],[180,90],[-180,90],[-180,-90]]] } } });
    map.addLayer({ id: 'dark-overlay', type: 'fill', source: 'dark-overlay',
      paint: { 'fill-color': '#0a0a1a', 'fill-opacity': 0.35 } });

    // Shared GeoJSON source for all drone positions
    map.addSource('drones', { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });

    // Circle layer for drone dots
    map.addLayer({ id: 'drone-circles', type: 'circle', source: 'drones',
      paint: {
        'circle-radius': 7,
        'circle-color': ['get', 'color'],
        'circle-stroke-width': 2,
        'circle-stroke-color': 'rgba(255,255,255,0.8)'
      }
    });

    // Symbol layer for drone labels
    map.addLayer({ id: 'drone-labels', type: 'symbol', source: 'drones',
      layout: {
        'text-field': ['get', 'deviceId'],
        'text-font': ['Open Sans Semibold'],
        'text-size': 12,
        'text-offset': [0, -1.5],
        'text-anchor': 'bottom'
      },
      paint: {
        'text-color': '#ffffff',
        'text-halo-color': 'rgba(0,0,0,0.8)',
        'text-halo-width': 2
      }
    });

    // Click handler for drone circles
    map.on('click', 'drone-circles', function(e) {
      if (e.features && e.features.length > 0) {
        showPopup(e.features[0].properties.deviceId);
      }
    });
    map.on('mouseenter', 'drone-circles', function() {
      map.getCanvas().style.cursor = 'pointer';
    });
    map.on('mouseleave', 'drone-circles', function() {
      map.getCanvas().style.cursor = '';
    });

    // Load initial positions after map is ready
    loadInitialPositions();
    // Then connect to AppSync
    connectWebSocket();
  });

  map.addControl(new maplibregl.NavigationControl(), 'top-left');

  // ---- Drone management ----
  function ensureDrone(deviceId) {
    if (droneState[deviceId]) return droneState[deviceId];
    var color = COLORS[droneIndex % COLORS.length];
    droneIndex++;
    var drone = {
      color: color, positions: [], lastData: null,
      sourceId: 'trail-' + deviceId, layerId: 'trail-layer-' + deviceId
    };
    droneState[deviceId] = drone;
    if (map.loaded()) addTrail(drone); else map.on('load', function() { addTrail(drone); });
    updateLegend();
    return drone;
  }

  function addTrail(drone) {
    if (map.getSource(drone.sourceId)) return;
    map.addSource(drone.sourceId, { type: 'geojson',
      data: { type: 'Feature', geometry: { type: 'LineString', coordinates: [] } } });
    map.addLayer({ id: drone.layerId, type: 'line', source: drone.sourceId,
      layout: { 'line-join': 'round', 'line-cap': 'round' },
      paint: { 'line-color': drone.color, 'line-width': 2.5, 'line-opacity': 0.6 } });
  }

  function updateTrail(drone) {
    var src = map.getSource(drone.sourceId);
    if (!src) return;
    var coords = drone.positions.map(function(p) { return [p.lon, p.lat]; });
    src.setData({ type: 'Feature', geometry: { type: 'LineString', coordinates: coords } });
  }

  function updateDroneSource() {
    var src = map.getSource('drones');
    if (!src) return;
    var features = [];
    Object.keys(droneState).forEach(function(id) {
      var d = droneState[id].lastData;
      if (!d) return;
      features.push({
        type: 'Feature',
        geometry: { type: 'Point', coordinates: [d.lon, d.lat] },
        properties: { deviceId: id, color: droneState[id].color }
      });
    });
    src.setData({ type: 'FeatureCollection', features: features });
  }

  function updateDrone(data) {
    var drone = ensureDrone(data.deviceId);
    drone.lastData = data;
    drone.positions.push({ lat: data.lat, lon: data.lon });
    if (drone.positions.length > MAX_TRAIL) drone.positions.shift();
    updateTrail(drone);
    updateDroneSource();
  }

  function showPopup(deviceId) {
    var drone = droneState[deviceId];
    if (!drone || !drone.lastData) return;
    if (currentPopup) { currentPopup.remove(); currentPopup = null; }
    var d = drone.lastData;
    var altStr = d.alt != null ? d.alt.toFixed(1) + ' m' : 'N/A';
    var velStr = d.vel != null ? d.vel.toFixed(1) + ' m/s' : 'N/A';
    var satStr = d.sats != null ? String(d.sats) : 'N/A';
    var tsStr  = d.ts ? new Date(d.ts).toLocaleString() : 'N/A';
    var html = '<div class="drone-popup">'
      + '<div class="popup-header"><div class="dot" style="background:' + drone.color
      + ';box-shadow:0 0 6px ' + drone.color + '"></div><div class="device-name">'
      + escapeHtml(deviceId) + '</div></div>'
      + row('Latitude', d.lat.toFixed(6)) + row('Longitude', d.lon.toFixed(6))
      + row('Altitude', altStr) + row('Speed', velStr) + row('Satellites', satStr)
      + row('Last Update', tsStr) + '</div>';
    currentPopup = new maplibregl.Popup({ closeOnClick: true, maxWidth: '280px' })
      .setLngLat([d.lon, d.lat]).setHTML(html).addTo(map);
  }
  function row(label, value) {
    return '<div class="popup-row"><span class="popup-label">' + label
      + '</span><span class="popup-value">' + value + '</span></div>';
  }

  function updateLegend() {
    var ids = Object.keys(droneState);
    if (!ids.length) { legendEl.style.display = 'none'; return; }
    legendEl.style.display = 'block';
    legendItems.innerHTML = '';
    ids.forEach(function(id) {
      var d = droneState[id];
      var item = document.createElement('div'); item.className = 'legend-item';
      var dot = document.createElement('div'); dot.className = 'legend-dot'; dot.style.background = d.color;
      var name = document.createElement('span'); name.className = 'legend-name'; name.textContent = id;
      item.appendChild(dot); item.appendChild(name); legendItems.appendChild(item);
    });
  }

  // ---- Load initial positions from Location Service ----
  function loadInitialPositions() {
    if (!INITIAL_POSITIONS || !INITIAL_POSITIONS.length) {
      setStatus('No drones found yet -- waiting for data...', 'warning');
      return;
    }
    INITIAL_POSITIONS.forEach(function(p) { updateDrone(p); });
    setStatus('Loaded ' + INITIAL_POSITIONS.length + ' drone(s) from history', 'active');
    statusRight.textContent = fmtTime(new Date());
  }

  // ---- AppSync Events WebSocket ----
  function amzDate() {
    return new Date().toISOString().replace(/[:\-]|\.\d{3}/g, '');
  }

  function toBase64Url(str) {
    return btoa(str).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
  }

  function makeAuth() {
    return { host: HTTP_HOST, 'x-amz-date': amzDate(), 'x-api-key': API_KEY };
  }

  function connectWebSocket() {
    if (!REALTIME_HOST || !API_KEY) {
      setStatus('AppSync not configured', 'error');
      return;
    }
    var auth = makeAuth();
    var encodedAuth = toBase64Url(JSON.stringify(auth));

    try {
      ws = new WebSocket(
        'wss://' + REALTIME_HOST + '/event/realtime',
        ['aws-appsync-event-ws', 'header-' + encodedAuth]
      );
    } catch (e) {
      setStatus('WebSocket error: ' + e.message, 'error');
      scheduleReconnect();
      return;
    }

    ws.onopen = function() {
      ws.send(JSON.stringify({ type: 'connection_init' }));
    };

    ws.onmessage = function(evt) {
      var msg;
      try { msg = JSON.parse(evt.data); } catch (e) { return; }

      if (msg.type === 'connection_ack') {
        var subAuth = makeAuth();
        ws.send(JSON.stringify({
          type: 'subscribe',
          id: 'drone-sub-' + Date.now(),
          channel: CHANNEL,
          authorization: subAuth,
          payload: { channel: CHANNEL, extensions: { authorization: subAuth } }
        }));
      } else if (msg.type === 'subscribe_success') {
        setStatus('Connected -- streaming live positions', 'active');
      } else if (msg.type === 'data') {
        handleDataEvent(msg);
      } else if (msg.type === 'ka') {
        // keepalive -- ignore
      } else if (msg.type === 'error') {
        console.warn('AppSync error:', msg);
      }
    };

    ws.onclose = function() {
      setStatus('Disconnected -- reconnecting...', 'warning');
      scheduleReconnect();
    };

    ws.onerror = function() {
      setStatus('Connection error -- reconnecting...', 'error');
    };
  }

  function handleDataEvent(msg) {
    // msg.event is a JSON string (or array of JSON strings)
    var events = [];
    if (typeof msg.event === 'string') {
      events.push(msg.event);
    } else if (Array.isArray(msg.events)) {
      events = msg.events;
    } else if (typeof msg.event === 'object') {
      events.push(JSON.stringify(msg.event));
    }

    events.forEach(function(raw) {
      var data;
      try { data = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { return; }
      if (data.deviceId && data.lat != null && data.lon != null) {
        updateDrone(data);
        var count = Object.keys(droneState).length;
        setStatus('Tracking ' + count + ' drone(s)', 'active');
        statusRight.textContent = 'Last update: ' + fmtTime(new Date());
      }
    });
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function() {
      reconnectTimer = null;
      connectWebSocket();
    }, RECONNECT_MS);
  }

})();
</script>
</body>
</html>"""
