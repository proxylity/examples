import { createHash, createHmac, timingSafeEqual, randomUUID } from 'node:crypto';
import { domainToASCII } from 'node:url';
import ipaddr from 'ipaddr.js';

export function ruleKey(kind, value) {
  value = String(value || '').trim();
  if (kind === 'domain') {
    const name = domainToASCII(value.replace(/^\./, '').replace(/\.$/, '').toLowerCase());
    if (!name || ipaddr.isValid(name) || name.length > 253 || !name.split('.').every(x => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(x))) throw Error('Invalid domain');
    return name;
  }
  if (kind === 'asn') {
    const n = value.replace(/^AS/i, '');
    if (!/^[0-9]{1,10}$/.test(n) || BigInt(n) < 1n || BigInt(n) > 4294967295n) throw Error('Invalid ASN');
    return `AS#${Number(n)}`;
  }
  if (kind === 'ip') {
    const parts = value.split('/');
    if (parts.length > 2 || !ipaddr.isValid(parts[0]) || (parts.length === 2 && !/^(0|[1-9][0-9]{0,2})$/.test(parts[1]))) throw Error('Invalid IP or CIDR');
    const addr = ipaddr.parse(parts[0]);
    if (addr.zoneId || (addr.kind() === 'ipv6' && addr.isIPv4MappedAddress())) throw Error('Use an unscoped IPv4 or IPv6 address');
    const bits = addr.kind() === 'ipv4' ? 32 : 128;
    const prefix = parts.length === 2 ? Number(parts[1]) : bits;
    if (prefix > bits) throw Error('Invalid CIDR prefix');
    const bytes = addr.toByteArray().map((b, i) => b & (prefix >= (i + 1) * 8 ? 255 : prefix <= i * 8 ? 0 : (255 << (8 - (prefix - i * 8))) & 255));
    const network = ipaddr.fromByteArray(bytes);
    return `IP#${network.toString()}/${prefix}`;
  }
  throw Error('Invalid rule type');
}

export function kindOf(key) {
  if (typeof key !== 'string') return null;
  try {
    if (key.startsWith('AS#')) return ruleKey('asn', key.slice(3)) === key ? 'asn' : null;
    if (key.startsWith('IP#')) return ruleKey('ip', key.slice(3)) === key ? 'ip' : null;
    return ruleKey('domain', key) === key ? 'domain' : null;
  } catch { return null; }
}

function stable(value) {
  if (value instanceof Set) return [...value].sort().map(stable);
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, stable(value[k])]));
  return value;
}

export function version(item, secret) {
  return createHmac('sha256', secret).update(JSON.stringify(stable(item ?? null))).digest('base64url');
}

export function sameVersion(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string') return false;
  const x = Buffer.from(a), y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

export function snapshotCondition(item) {
  if (!item) return { ConditionExpression: 'attribute_not_exists(PK)' };
  const fields = new Set([...Object.keys(item).filter(k => k !== 'PK' && k !== 'SK'),
    'revision', 'blocked', 'allowed', 'managedBy', 'sourceLists', 'ManagedPK', 'ManagedSK',
    'GSI1PK', 'GSI1SK', 'asn', 'redirect']);
  const names = {}, values = {}, expressions = ['attribute_exists(PK)'];
  let i = 0;
  for (const field of fields) {
    const n = `#c${i}`, v = `:c${i++}`;
    names[n] = field;
    if (Object.hasOwn(item, field)) { values[v] = item[field]; expressions.push(`${n} = ${v}`); }
    else expressions.push(`attribute_not_exists(${n})`);
  }
  return { ConditionExpression: expressions.join(' AND '), ExpressionAttributeNames: names, ...(Object.keys(values).length ? { ExpressionAttributeValues: values } : {}) };
}

export function ruleAfter(before, key, action, redirect) {
  if (!['block', 'allow', 'delete', 'redirect'].includes(action)) throw Error('Invalid action');
  if (action === 'delete') return null;
  const after = { ...(before || {}), PK: key, SK: key, blocked: action === 'block', allowed: action === 'allow', revision: createHash('sha256').update(randomUUID()).digest('hex') };
  delete after.redirect;
  if (action === 'redirect') after.redirect = redirect;
  for (const attr of ['managedBy', 'sourceLists', 'ManagedPK', 'ManagedSK']) delete after[attr];
  if (key.startsWith('AS#')) {
    after.asn = Number(key.slice(3));
    if (action === 'block' || action === 'redirect') { after.GSI1PK = 'BLOCKED_ASNS'; after.GSI1SK = key.slice(3); }
    else { delete after.GSI1PK; delete after.GSI1SK; }
  }
  return after;
}
