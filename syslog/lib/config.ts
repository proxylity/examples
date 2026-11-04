// Loads and validates syslog.config.json and syslog.peers.json at synth time. Every `cdk synth`
// and `cdk deploy` runs these checks, so a bad config never reaches CloudFormation.
//
// syslog.peers.json is the single source of truth for sites: the same array becomes the listener's
// WireGuard peers, one site-silence alarm per site, and the per-site dashboard series.

import * as fs from 'fs';
import * as net from 'net';
import * as path from 'path';

export type LockMode = 'GOVERNANCE' | 'COMPLIANCE';

export interface Site {
  /** Readable label used in alarm names and on the dashboard. Defaults to site-<n>. */
  readonly name: string;
  readonly publicKey: string;
}

export interface SyslogConfig {
  readonly network: { readonly clientCidrs: string[]; readonly collectorAddress: string };
  readonly sites: Site[];
  readonly retention: {
    readonly logGroupDays: number;
    readonly archiveDays: number;
    readonly archiveLockMode: LockMode;
    readonly archiveGlacierIrDays: number;
    readonly analyticsDays: number;
    readonly analyticsGlacierIrDays: number;
    readonly athenaResultsDays: number;
    readonly keyRotationDays: number;
  };
  readonly alarms: {
    readonly email: string;
    readonly pipelineSilenceSeconds: number;
    readonly siteSilenceSeconds: number;
    readonly freshnessSeconds: number;
  };
  readonly ingest: { readonly logsFilterExpression: string };
  readonly security: {
    readonly governanceBypassPrincipalArn: string;
    readonly keyAdminPrincipalArns: string[];
    readonly replicaBucketArn: string;
    readonly replicaKmsKeyArn: string;
  };
  readonly siem: {
    readonly hecEndpoint: string;
    readonly hecEndpointType: 'Event' | 'Raw';
    readonly hecTokenSecretArn: string;
    readonly subscriptionFilterPattern: string;
  };
}

const KEY_PATTERN = /^[A-Za-z0-9+/]{43}=$/;
const SITE_NAME_PATTERN = /^[a-z0-9][a-z0-9-]{0,31}$/;
export const LOG_RETENTION_DAYS = [1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180, 365, 400, 545, 731,
  1096, 1827, 2192, 2557, 2922, 3288, 3653];

class Problems {
  readonly errors: string[] = [];
  readonly warnings: string[] = [];
  error(message: string): void { this.errors.push(message); }
  warn(message: string): void { this.warnings.push(message); }
}

function readJson(file: string): any {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf-8'));
  } catch (e) {
    throw new Error(`Can't read ${file}: ${(e as Error).message}`);
  }
}

function section(raw: any, name: string, p: Problems): any {
  const value = raw?.[name];
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    p.error(`syslog.config.json: missing section "${name}".`);
    return {};
  }
  return value;
}

function str(obj: any, where: string, p: Problems): string {
  const value = obj?.[where.split('.').pop()!];
  if (typeof value !== 'string') {
    p.error(`syslog.config.json: ${where} must be a string.`);
    return '';
  }
  return value.trim();
}

function strList(obj: any, where: string, p: Problems): string[] {
  const value = obj?.[where.split('.').pop()!];
  if (!Array.isArray(value) || !value.every((v) => typeof v === 'string')) {
    p.error(`syslog.config.json: ${where} must be a list of strings.`);
    return [];
  }
  return value.map((v: string) => v.trim());
}

function int(obj: any, where: string, p: Problems, min: number, max = Number.MAX_SAFE_INTEGER): number {
  const value = obj?.[where.split('.').pop()!];
  if (!Number.isInteger(value) || value < min || value > max) {
    p.error(`syslog.config.json: ${where} must be a whole number from ${min} to ${max}.`);
    return min;
  }
  return value;
}

// ---- CIDR helpers (IPv4 and IPv6) ----------------------------------------------------------

interface Range { readonly version: 4 | 6; readonly first: bigint; readonly last: bigint; readonly prefix: number }

function ipToBigInt(address: string): { version: 4 | 6; value: bigint } | undefined {
  const version = net.isIP(address);
  if (version === 4) {
    return { version, value: address.split('.').reduce((acc, octet) => (acc << 8n) | BigInt(Number(octet)), 0n) };
  }
  if (version === 6) {
    let text = address.toLowerCase();
    // Expand an embedded IPv4 tail (::ffff:1.2.3.4).
    const v4 = text.match(/(\d+\.\d+\.\d+\.\d+)$/);
    if (v4) {
      const n = ipToBigInt(v4[1])!.value;
      text = text.slice(0, -v4[1].length) + `${(n >> 16n).toString(16)}:${(n & 0xffffn).toString(16)}`;
    }
    const [head, tail] = text.split('::');
    const headParts = head ? head.split(':') : [];
    const tailParts = tail !== undefined && tail !== '' ? tail.split(':') : [];
    const fill = text.includes('::') ? 8 - headParts.length - tailParts.length : 0;
    const parts = [...headParts, ...Array(fill).fill('0'), ...tailParts];
    return { version, value: parts.reduce((acc, h) => (acc << 16n) | BigInt(parseInt(h, 16)), 0n) };
  }
  return undefined;
}

function parseCidr(value: string): Range | undefined {
  const [address, prefixText, extra] = value.split('/');
  const ip = ipToBigInt(address ?? '');
  if (!ip || extra !== undefined) return undefined;
  const bits = ip.version === 4 ? 32 : 128;
  const prefix = prefixText === undefined ? bits : Number(prefixText);
  if (!Number.isInteger(prefix) || prefix < 0 || prefix > bits || (prefixText !== undefined && !/^\d+$/.test(prefixText))) {
    return undefined;
  }
  const hostBits = BigInt(bits - prefix);
  const first = (ip.value >> hostBits) << hostBits;
  return { version: ip.version, first, last: first + (1n << hostBits) - 1n, prefix };
}

// ---- Loading -------------------------------------------------------------------------------

export function loadConfig(configDir: string, names: { stackName?: string; region?: string }): SyslogConfig {
  const p = new Problems();
  const raw = readJson(path.join(configDir, 'syslog.config.json'));
  const peersRaw = readJson(path.join(configDir, 'syslog.peers.json'));

  const network = section(raw, 'Network', p);
  const retention = section(raw, 'Retention', p);
  const alarms = section(raw, 'Alarms', p);
  const ingest = section(raw, 'Ingest', p);
  const security = section(raw, 'Security', p);
  const siem = section(raw, 'Siem', p);

  const config: SyslogConfig = {
    network: {
      clientCidrs: strList(network, 'Network.ClientCidrs', p),
      collectorAddress: str(network, 'Network.CollectorAddress', p),
    },
    sites: [],
    retention: {
      logGroupDays: int(retention, 'Retention.LogGroupDays', p, 1),
      archiveDays: int(retention, 'Retention.ArchiveDays', p, 1),
      archiveLockMode: str(retention, 'Retention.ArchiveLockMode', p) as LockMode,
      archiveGlacierIrDays: int(retention, 'Retention.ArchiveGlacierIrDays', p, 0),
      analyticsDays: int(retention, 'Retention.AnalyticsDays', p, 1),
      analyticsGlacierIrDays: int(retention, 'Retention.AnalyticsGlacierIrDays', p, 0),
      athenaResultsDays: int(retention, 'Retention.AthenaResultsDays', p, 1),
      keyRotationDays: int(retention, 'Retention.KeyRotationDays', p, 90, 2560),
    },
    alarms: {
      email: str(alarms, 'Alarms.Email', p),
      pipelineSilenceSeconds: int(alarms, 'Alarms.PipelineSilenceSeconds', p, 300, 86400),
      siteSilenceSeconds: int(alarms, 'Alarms.SiteSilenceSeconds', p, 300, 86400),
      freshnessSeconds: int(alarms, 'Alarms.FreshnessSeconds', p, 300),
    },
    ingest: { logsFilterExpression: str(ingest, 'Ingest.LogsFilterExpression', p) },
    security: {
      governanceBypassPrincipalArn: str(security, 'Security.GovernanceBypassPrincipalArn', p),
      keyAdminPrincipalArns: strList(security, 'Security.KeyAdminPrincipalArns', p),
      replicaBucketArn: str(security, 'Security.ReplicaBucketArn', p),
      replicaKmsKeyArn: str(security, 'Security.ReplicaKmsKeyArn', p),
    },
    siem: {
      hecEndpoint: str(siem, 'Siem.HecEndpoint', p),
      hecEndpointType: str(siem, 'Siem.HecEndpointType', p) as 'Event' | 'Raw',
      hecTokenSecretArn: str(siem, 'Siem.HecTokenSecretArn', p),
      subscriptionFilterPattern: str(siem, 'Siem.SubscriptionFilterPattern', p),
    },
  };

  // Sites.
  const peers = peersRaw?.Peers;
  if (!Array.isArray(peers) || peers.length === 0 || Object.keys(peersRaw).some((k) => k !== 'Peers')) {
    p.error('syslog.peers.json: must be { "Peers": [ ... ] } with at least one peer.');
  } else {
    peers.forEach((peer: any, i: number) => {
      const where = `syslog.peers.json Peers[${i}]`;
      const name = peer?.Name === undefined ? `site-${i + 1}` : String(peer.Name);
      if (!SITE_NAME_PATTERN.test(name)) {
        p.error(`${where}: Name '${name}' must be 1-32 lowercase letters, digits and hyphens.`);
      }
      const publicKey = String(peer?.PublicKey ?? '');
      if (!KEY_PATTERN.test(publicKey) || Buffer.from(publicKey, 'base64').length !== 32) {
        p.error(`${where}: PublicKey '${publicKey}' isn't a WireGuard public key (44 base64 characters ending in '=').`);
      }
      if (peer && typeof peer === 'object' && 'AllowedIPs' in peer) {
        p.error(`${where}: AllowedIPs isn't used by this example. Remove it. To limit who can connect, set ` +
          "Network.ClientCidrs; the site's key identifies the site.");
      }
      config.sites.push({ name, publicKey });
    });
  }
  for (const [label, values] of [['names', config.sites.map((s) => s.name)], ['public keys', config.sites.map((s) => s.publicKey)]] as const) {
    const dupes = [...new Set(values.filter((v, i) => values.indexOf(v) !== i))];
    if (dupes.length) p.error(`syslog.peers.json: duplicate site ${label}: ${dupes.join(', ')}.`);
  }

  // The collector is a tunnel address, so it must be a plain IP address.
  const collector = parseCidr(config.network.collectorAddress);
  if (!collector || config.network.collectorAddress.includes('/')) {
    p.error(`syslog.config.json: Network.CollectorAddress '${config.network.collectorAddress}' isn't an IP address.`);
  }
  config.network.clientCidrs.forEach((text, i) => {
    const range = parseCidr(text);
    if (!range) p.error(`syslog.config.json: Network.ClientCidrs[${i}] '${text}' isn't a CIDR.`);
    else if (range.prefix === 0) p.warn(`Network.ClientCidrs[${i}] is ${text}: any address can attempt a handshake (WireGuard keys still authenticate).`);
  });
  if (config.network.clientCidrs.length === 0) p.error('syslog.config.json: Network.ClientCidrs must list at least one CIDR.');

  // Retention.
  const r = config.retention;
  if (r.archiveLockMode !== 'GOVERNANCE' && r.archiveLockMode !== 'COMPLIANCE') {
    p.error('syslog.config.json: Retention.ArchiveLockMode must be GOVERNANCE or COMPLIANCE.');
  } else if (r.archiveLockMode === 'COMPLIANCE') {
    p.warn('Retention.ArchiveLockMode is COMPLIANCE: nobody, including root, can shorten or remove retention on archived objects.');
  }
  if (!LOG_RETENTION_DAYS.includes(r.logGroupDays)) {
    p.error(`syslog.config.json: Retention.LogGroupDays ${r.logGroupDays} isn't a CloudWatch Logs retention value (${LOG_RETENTION_DAYS.join(', ')}).`);
  }
  if (r.archiveGlacierIrDays && r.archiveGlacierIrDays >= r.archiveDays) p.error('Retention.ArchiveGlacierIrDays must be less than ArchiveDays.');
  if (r.analyticsGlacierIrDays && r.analyticsGlacierIrDays >= r.analyticsDays) p.error('Retention.AnalyticsGlacierIrDays must be less than AnalyticsDays.');
  for (const key of ['pipelineSilenceSeconds', 'siteSilenceSeconds'] as const) {
    if (config.alarms[key] % 60) p.error(`syslog.config.json: Alarms.${key[0].toUpperCase()}${key.slice(1)} must be a multiple of 60.`);
  }

  // Optional features.
  const s = config.security;
  if (Boolean(s.replicaBucketArn) !== Boolean(s.replicaKmsKeyArn)) p.error('Security.ReplicaBucketArn and ReplicaKmsKeyArn must be set together.');
  for (const arn of [s.governanceBypassPrincipalArn, ...s.keyAdminPrincipalArns, s.replicaBucketArn, s.replicaKmsKeyArn]) {
    if (arn && !arn.startsWith('arn:')) p.error(`syslog.config.json: '${arn}' isn't an ARN.`);
  }
  if (config.siem.hecEndpoint) {
    if (!config.siem.hecEndpoint.startsWith('https://')) p.error('Siem.HecEndpoint must start with https://.');
    if (!config.siem.hecTokenSecretArn.startsWith('arn:')) p.error('Siem.HecTokenSecretArn is required when HecEndpoint is set.');
    if (!['Event', 'Raw'].includes(config.siem.hecEndpointType)) p.error('Siem.HecEndpointType must be Event or Raw.');
  }

  // Names: buckets are <stack>-<region>-analytics-<account>, lowercase, at most 63 characters.
  if (names.stackName) {
    if (!/^[a-z][a-z0-9-]*$/.test(names.stackName)) {
      p.error(`Stack name '${names.stackName}' must be lowercase letters, digits and hyphens (it's used in bucket names).`);
    }
    if (names.region) {
      const longest = `${names.stackName}-${names.region}-analytics-123456789012`.length;
      if (longest > 63) {
        p.error(`Stack name '${names.stackName}' is too long for ${names.region}: bucket names would be ${longest} ` +
          `characters (maximum 63). Use at most ${63 - longest + names.stackName.length} characters.`);
      }
    }
  }

  for (const w of p.warnings) console.warn(`WARNING: ${w}`);
  if (p.errors.length) {
    throw new Error(`Invalid configuration in ${path.resolve(configDir)}:\n  - ${p.errors.join('\n  - ')}`);
  }
  return config;
}
