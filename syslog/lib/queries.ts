// Saved Athena queries. `syslog` is the Parquet analytics table; `syslog_archive` is the thin JSON
// table over the WORM archive, whose `data` is the original message as base64. `inner` is a SQL
// reserved word, hence "inner". dt/hr are Firehose arrival partitions (UTC): filter on dt to limit
// the data scanned, and on receivedat for exact windows.

export interface SavedQuery {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly sql: string;
}

const since = (interval: string) => `format_datetime(current_timestamp - interval ${interval}, 'yyyy-MM-dd')`;

export const SAVED_QUERIES: SavedQuery[] = [
  {
    id: 'HighSeverity',
    name: '1 High severity (crit, alert, emerg), last 24 hours',
    description: 'Messages with severity 2 or lower, newest first.',
    sql: `SELECT receivedat,
       remote.peerkey AS site,
       "inner".sourceaddress AS device,
       data.hostname, data.app_name, data.severity_name, data.message
FROM syslog
WHERE dt >= ${since("'1' day")}
  AND receivedat >= format_datetime(current_timestamp - interval '24' hour, 'yyyy-MM-dd''T''HH:mm:ss')
  AND data.severity <= 2
ORDER BY receivedat DESC
LIMIT 500;`,
  },
  {
    id: 'TopSitesDevices',
    name: '2 Top sites and devices, last 7 days',
    description: 'Message counts per site (WireGuard key) and device (inner source address).',
    sql: `SELECT remote.peerkey AS site,
       "inner".sourceaddress AS device,
       count(*) AS messages,
       min(receivedat) AS first_seen,
       max(receivedat) AS last_seen
FROM syslog
WHERE dt >= ${since("'7' day")}
GROUP BY 1, 2
ORDER BY messages DESC
LIMIT 100;`,
  },
  {
    id: 'VolumeBySeverity',
    name: '3 Volume by hour and severity, last 2 days',
    description: 'Hourly message counts by severity (unparsed messages counted separately).',
    sql: `SELECT substr(receivedat, 1, 13) AS hour_utc,
       coalesce(data.severity_name, 'unparsed') AS severity,
       count(*) AS messages
FROM syslog
WHERE dt >= ${since("'2' day")}
GROUP BY 1, 2
ORDER BY 1, 2;`,
  },
  {
    id: 'ParseFailures',
    name: '4 Parse failures by device, last 7 days',
    description: "Devices sending messages the syslog formatter couldn't parse, with a sample.",
    sql: `SELECT remote.peerkey AS site,
       "inner".sourceaddress AS device,
       count(*) AS failures,
       arbitrary(data.raw) AS sample
FROM syslog
WHERE dt >= ${since("'7' day")}
  AND data.parse_ok = false
GROUP BY 1, 2
ORDER BY failures DESC;`,
  },
  {
    id: 'HostnameInconsistency',
    name: '5 Hostname inconsistency per device, last 7 days',
    description: 'Devices, identified by site key and inner address, reporting more than one hostname. Hostnames and inner addresses are both self-reported by the site.',
    sql: `SELECT remote.peerkey AS site,
       "inner".sourceaddress AS device,
       count(DISTINCT data.hostname) AS hostnames,
       array_agg(DISTINCT data.hostname) AS names
FROM syslog
WHERE dt >= ${since("'7' day")}
  AND data.hostname IS NOT NULL
GROUP BY 1, 2
HAVING count(DISTINCT data.hostname) > 1
ORDER BY hostnames DESC;`,
  },
  {
    id: 'EvidenceScope',
    name: '6 Archive: evidence objects for a site, device and window',
    description: 'Lists the archive objects ("$path") holding a site\'s device messages in a time window. Edit the placeholders first.',
    sql: `SELECT "$path" AS object,
       count(*) AS messages,
       min(receivedat) AS first_received,
       max(receivedat) AS last_received
FROM syslog_archive
WHERE dt BETWEEN '2026-11-01' AND '2026-11-02'
  AND receivedat BETWEEN '2026-11-01T00:00:00' AND '2026-11-02T23:59:59'
  AND remote.peerkey = 'REPLACE-WITH-SITE-PUBLIC-KEY'
  AND "inner".sourceaddress = 'REPLACE-WITH-DEVICE-ADDRESS'
GROUP BY 1
ORDER BY 3;`,
  },
  {
    id: 'Completeness',
    name: '7 Archive: completeness per site per hour, last 24 hours',
    description: "Messages per site per hour, including hours with zero messages. Only shows gaps after ingestion; UDP loss before the gateway isn't visible.",
    sql: `WITH hours AS (
  SELECT format_datetime(h, 'yyyy-MM-dd''T''HH') AS hour_utc
  FROM UNNEST(sequence(date_trunc('hour', current_timestamp - interval '23' hour),
                       date_trunc('hour', current_timestamp),
                       interval '1' hour)) AS t(h)
),
sites AS (
  SELECT DISTINCT remote.peerkey AS site
  FROM syslog_archive
  WHERE dt >= ${since("'7' day")}
),
counts AS (
  SELECT remote.peerkey AS site, substr(receivedat, 1, 13) AS hour_utc, count(*) AS messages
  FROM syslog_archive
  WHERE dt >= ${since("'2' day")}
  GROUP BY 1, 2
)
SELECT s.site, h.hour_utc, coalesce(c.messages, 0) AS messages
FROM sites s
CROSS JOIN hours h
LEFT JOIN counts c ON c.site = s.site AND c.hour_utc = h.hour_utc
ORDER BY s.site, h.hour_utc;`,
  },
  {
    id: 'DecodedLookup',
    name: '8 Archive: decoded message lookup, today',
    description: 'Decodes the original bytes and searches them. Edit the search text first.',
    sql: `SELECT receivedat,
       remote.peerkey AS site,
       "inner".sourceaddress AS device,
       from_utf8(from_base64(data)) AS original_message,
       "$path" AS object
FROM syslog_archive
WHERE dt = format_datetime(current_timestamp, 'yyyy-MM-dd')
  AND strpos(from_utf8(from_base64(data)), 'REPLACE-WITH-SEARCH-TEXT') > 0
ORDER BY receivedat DESC
LIMIT 100;`,
  },
  {
    id: 'CrossCheck',
    name: '9 Archive vs analytics hourly count cross-check, last 24 hours',
    description: "Compares hourly message counts in the archive and analytics tables by receive time. A non-zero difference means one destination lost or hasn't yet delivered messages.",
    sql: `WITH a AS (
  SELECT substr(receivedat, 1, 13) AS hour_utc, count(*) AS n
  FROM syslog_archive
  WHERE dt >= ${since("'2' day")}
    AND receivedat >= format_datetime(current_timestamp - interval '24' hour, 'yyyy-MM-dd''T''HH')
  GROUP BY 1
),
b AS (
  SELECT substr(receivedat, 1, 13) AS hour_utc, count(*) AS n
  FROM syslog
  WHERE dt >= ${since("'2' day")}
    AND receivedat >= format_datetime(current_timestamp - interval '24' hour, 'yyyy-MM-dd''T''HH')
  GROUP BY 1
)
SELECT coalesce(a.hour_utc, b.hour_utc) AS hour_utc,
       coalesce(a.n, 0) AS archive,
       coalesce(b.n, 0) AS analytics,
       coalesce(a.n, 0) - coalesce(b.n, 0) AS difference
FROM a FULL OUTER JOIN b ON a.hour_utc = b.hour_utc
ORDER BY 1;`,
  },
];
