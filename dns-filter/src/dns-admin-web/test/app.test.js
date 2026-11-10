import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { sealData } from 'iron-session';
import { createApp, configFromEnv } from '../app.js';
import { ruleKey, ruleAfter, version } from '../policy.js';
import { services } from '../services.js';

const secret = 'a'.repeat(48);
const config = configFromEnv({ TABLE_NAME: 'table', HOME_REGION: 'us-east-1', BUILD_PROJECT: 'build', BLOCKLIST_PROFILE: 'Balanced', COGNITO_ISSUER: 'https://issuer.example/pool', COGNITO_CLIENT_ID: 'client', COGNITO_DOMAIN: 'https://login.example', SESSION_SECRET_ARN: 'secret', PUBLIC_URL: 'https://admin.example' });
let item = { PK: 'example.com', SK: 'example.com', blocked: true, managedBy: 'blocklist-sync', sourceLists: new Set(['feed']), ManagedPK: 'BLOCKLIST_SYNC#1', ManagedSK: 'DOMAIN#example.com' };
const changes = [], builds = [];
const svc = {
  secret: async () => secret,
  loginUrl: async uri => ({ url: 'https://login.example/authorize', transaction: { state: 'state', verifier: 'verifier', nonce: 'nonce', uri } }),
  callback: async (url, tx) => {
    assert.equal(url.origin, 'https://admin.example'); assert.equal(tx.state, 'state');
    if (url.searchParams.get('state') !== tx.state) throw Error('bad state');
    return { sub: '<admin>', exp: Math.floor(Date.now() / 1000) + 3600, 'cognito:groups': ['admin'] };
  },
  get: async key => key === 'example.com' ? item : undefined,
  rules: async () => ({ Items: [item, { PK: 'IP#192.0.2.0/24', SK: 'IP#192.0.2.0/24', blocked: true },
    { PK: 'AS#42', SK: 'AS#42', blocked: true }, { PK: 'SYNC#STATE', SK: 'SYNC#STATE' }],
    LastEvaluatedKey: { PK: 'example.com', SK: 'example.com' } }),
  audit: async () => ({ Items: [{ actor: '<script>alert(1)</script>', PK: 'AUDIT', SK: 'x' }] }),
  change: async (...args) => { changes.push(args); },
  startBuild: async () => { builds.push(1); return { build: { id: 'build:123', buildStatus: 'IN_PROGRESS' } }; }
};
const server = http.createServer(createApp(config, svc));
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
after(() => server.close());
const base = `http://127.0.0.1:${server.address().port}`;
async function request(path, options = {}) { return fetch(base + path, { redirect: 'manual', ...options }); }
async function session(role = 'admin') {
  const data = await sealData({ sub: '<user>', role, csrf: 'csrf-token', exp: Math.floor(Date.now() / 1000) + 3600 }, { password: secret, ttl: 28800 });
  return `__Host-dns-session=${data}`;
}
function post(cookie, fields) { return { method: 'POST', headers: { cookie, 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(fields) }; }

test('anonymous routes and fixed callback origin, PKCE transaction and state', async () => {
  assert.equal((await request('/')).status, 401);
  const login = await request('/login', { headers: { host: 'evil.example' } });
  assert.equal(login.status, 303); assert.equal(login.headers.get('location'), 'https://login.example/authorize');
  const tx = login.headers.get('set-cookie').split(';')[0];
  assert.match(login.headers.get('set-cookie'), /HttpOnly; Secure; SameSite=Lax/);
  assert.equal((await request('/callback?code=x&state=bad', { headers: { cookie: tx, host: 'evil.example' } })).status, 400);
  const callback = await request('/callback?code=x&state=state', { headers: { cookie: tx, host: 'evil.example' } });
  assert.equal(callback.status, 303);
  assert.match(callback.headers.get('set-cookie'), /__Host-dns-session=/);
  assert.equal((await request('/callback?code=x&state=state')).status, 400);
});

test('viewer cannot mutate, CSRF is enforced, escaped output and policy pagination', async () => {
  const viewer = await session('viewer'), admin = await session();
  assert.equal((await request('/rules', post(viewer, { csrf: 'csrf-token', kind: 'domain', value: 'example.com', action: 'block', version: version(item, secret) }))).status, 403);
  assert.equal((await request('/sync', post(admin, { csrf: 'wrong' }))).status, 403);
  assert.equal(builds.length, 0);
  const audit = await (await request('/audit', { headers: { cookie: viewer } })).text();
  assert.ok(audit.includes('&lt;script&gt;')); assert.ok(!audit.includes('<script>'));
  const page = await (await request('/', { headers: { cookie: viewer } })).text();
  assert.ok(page.includes('&lt;user&gt;')); assert.ok(!page.includes('<user>'));
  const rules = await (await request('/rules', { headers: { cookie: viewer } })).text();
  assert.ok(rules.includes('example.com')); assert.ok(!rules.includes('Take ownership'));
  const found = await (await request('/rules?kind=domain&value=example.com', { headers: { cookie: admin } })).text();
  assert.ok(found.includes('aria-label="Block"'));
  assert.ok(!found.includes('Take ownership'));
  assert.equal((await request('/rules?kind=domain&value=%3Cbad%3E', { headers: { cookie: viewer } })).status, 400);
  assert.equal((await request('/rules?cursor=not-valid!', { headers: { cookie: viewer } })).status, 400);
});

test('notification links survive login and prefill a block form without mutating policy', async () => {
  const path = '/rules?kind=domain&value=new.example';
  const before = changes.length;
  const anonymous = await (await request(path)).text();
  assert.ok(anonymous.includes('/login?returnTo=' + encodeURIComponent(path)));
  const login = await request('/login?returnTo=' + encodeURIComponent(path));
  const tx = login.headers.get('set-cookie').split(';')[0];
  const callback = await request('/callback?code=x&state=state', { headers: { cookie: tx } });
  assert.equal(callback.status, 303);
  assert.equal(callback.headers.get('location'), path);
  const admin = await session();
  const page = await (await request(path, { headers: { cookie: admin } })).text();
  assert.ok(page.includes('name="value" value="new.example" required'));
  assert.ok(page.includes('action="/domains"'));
  assert.ok(!page.includes('<select'));
  assert.ok(page.includes('<button name="action" value="block" aria-label="Block"'));
  assert.equal(changes.length, before);
  const viewerPage = await (await request(path, { headers: { cookie: await session('viewer') } })).text();
  assert.ok(!viewerPage.includes('<h2>Create a rule</h2>'));
  const blocked = await request('/rules', post(admin, { csrf: 'csrf-token', kind: 'domain', value: 'new.example', action: 'block', version: version(null, secret) }));
  assert.equal(blocked.status, 303);
  assert.equal(changes.at(-1)[0], 'new.example');
  assert.equal(changes.at(-1)[2], 'block');
});

test('login destinations cannot redirect outside the configured UI or into auth routes', async () => {
  for (const destination of ['https://evil.example/rules', '//evil.example/rules', '/\\evil.example/rules', '/login', '/callback', '/rules#fragment']) {
    assert.equal((await request('/login?returnTo=' + encodeURIComponent(destination))).status, 400);
  }
});

test('rules use collapsed details, accessible touch buttons, persistent lookup and authenticated fragments', async () => {
  const admin = await session();
  const response = await request('/rules?kind=domain&value=example.com', { headers: { cookie: admin } });
  const html = await response.text();
  assert.ok(html.includes('font:1rem system-ui'));
  assert.ok(html.includes('height:2.75rem;padding:.5rem .75rem'));
  assert.ok(html.includes('select{padding-right:2rem}'));
  assert.ok(html.includes('<details><summary>Details</summary><pre>'));
  assert.ok(!html.includes('<details open'));
  assert.ok(html.includes('id="rule-lookup"'));
  assert.ok(html.includes('>Load rule</button>'));
  assert.ok(!html.includes('Find rule'));
  assert.ok(html.includes('aria-label="Redirect" title="Redirect"'));
  assert.ok(html.includes('<script src="/rules.js" defer>'));
  assert.ok(response.headers.get('content-security-policy').includes("script-src 'self'; connect-src 'self'"));
  const fragment = await (await request('/rules?kind=domain&value=missing.example&fragment=1', { headers: { cookie: admin } })).text();
  assert.ok(fragment.includes('No rule exists for'));
  assert.ok(fragment.includes('name="value" value="missing.example"'));
  assert.ok(!fragment.includes('<html'));
  assert.ok(!fragment.includes('id="rule-lookup"'));
  const viewer = await (await request('/rules?kind=domain&value=example.com&fragment=1', { headers: { cookie: await session('viewer') } })).text();
  assert.ok(!viewer.includes('method="post"'));
  assert.equal((await request('/rules?kind=domain&value=example.com&fragment=1')).status, 401);
  assert.equal((await request('/rules?kind=ip&value=bad&fragment=1', { headers: { cookie: admin } })).status, 400);
  const script = await request('/rules.js', { headers: { cookie: admin } });
  assert.equal(script.status, 200);
  assert.match(script.headers.get('content-type'), /javascript/);
  assert.equal((await request('/rules.js')).status, 401);
});

test('overview, sync and audit JSON is only presented inside closed details', async () => {
  const admin = await session();
  for (const path of ['/', '/sync', '/audit']) {
    const html = await (await request(path, { headers: { cookie: admin } })).text();
    assert.ok(html.includes('<details><summary>Details</summary><pre>'));
    assert.ok(!html.includes('<details open'));
    assert.equal((html.match(/<pre>/g) || []).length, (html.match(/<details><summary>Details<\/summary><pre>/g) || []).length);
  }
});

test('separate rule pages show only their own type and keep pagination, lookups and actions on that page', async () => {
  const admin = await session();
  for (const [path, title, kind, key, value] of [
    ['/domains', 'Domains', 'domain', 'example.com', 'new.example'],
    ['/ip-cidrs', 'IP/CIDRs', 'ip', 'IP#192.0.2.0/24', '198.51.100.0/24'],
    ['/asns', 'ASNs', 'asn', 'AS#42', 'AS43']
  ]) {
    const page = await (await request(path, { headers: { cookie: admin } })).text();
    assert.ok(page.includes(`<h1>${title}</h1>`));
    assert.ok(page.includes(`<a href="${path}" aria-current="page">`));
    assert.ok(page.includes(`<h2>${key}</h2>`));
    assert.equal((page.match(/<article>/g) || []).length, 1);
    assert.ok(page.includes(`href="${path}?cursor=`));
    assert.ok(page.includes(`id="rule-lookup" method="get" action="${path}"`));
    assert.ok(!page.includes('<select'));
    const lookup = await (await request(`${path}?value=${encodeURIComponent(value)}&fragment=1`, { headers: { cookie: admin } })).text();
    assert.ok(lookup.includes('Create a rule'));
    assert.ok(lookup.includes(`name="kind" value="${kind}"`));
    assert.ok(lookup.includes(`action="${path}"`));
    const result = await request(path, post(admin, { csrf: 'csrf-token', kind, value, action: 'block', version: version(null, secret) }));
    assert.equal(result.status, 303);
    assert.equal(result.headers.get('location'), `${path}?${new URLSearchParams({ value })}`);
    assert.equal((await request(path, post(admin, { csrf: 'csrf-token', kind: kind === 'domain' ? 'ip' : 'domain', value, action: 'block' }))).status, 400);
    const login = await request(`/login?returnTo=${encodeURIComponent(`${path}?value=${value}`)}`);
    const tx = login.headers.get('set-cookie').split(';')[0];
    const callback = await request('/callback?code=x&state=state', { headers: { cookie: tx } });
    assert.equal(callback.headers.get('location'), `${path}?value=${value}`);
  }
  assert.equal((await request('/rules?kind=invalid', { headers: { cookie: admin } })).status, 400);
});

test('SDK browse filters each rule type without unbounded scans or changing cursors', async () => {
  const commands = [];
  const fake = { send: async cmd => { commands.push(cmd); return {}; } };
  const service = services(config, { db: fake, secrets: fake, builds: fake });
  const cursor = { PK: 'example.com', SK: 'example.com' };
  for (const kind of ['domain', 'ip', 'asn']) {
    await service.rules(cursor, kind);
    const input = commands.at(-1).input;
    assert.equal(input.Limit, 50);
    assert.deepEqual(input.ExclusiveStartKey, cursor);
    assert.ok(input.FilterExpression.includes('PK = SK'));
    if (kind === 'domain') assert.ok(input.FilterExpression.includes('NOT begins_with'));
    else assert.ok(input.FilterExpression.includes(`begins_with(PK, :${kind})`));
  }
  await assert.rejects(() => service.rules(cursor, 'bad'), /Invalid rule type/);
});

test('admin conditional ownership changes, conflict, validation, and start build', async () => {
  const admin = await session();
  let res = await request('/rules', post(admin, { csrf: 'csrf-token', kind: 'domain', value: 'example.com', action: 'allow', version: version(item, secret) }));
  assert.equal(res.status, 303); assert.equal(changes.at(-1)[2], 'allow'); assert.equal(changes.at(-1)[3], '<user>');
  res = await request('/rules', post(admin, { csrf: 'csrf-token', kind: 'domain', value: 'example.com', action: 'block', version: 'stale' }));
  assert.equal(res.status, 409);
  res = await request('/rules', post(admin, { csrf: 'csrf-token', kind: 'domain', value: 'example.com', action: 'delete', version: version(item, secret) }));
  assert.equal(res.status, 400);
  res = await request('/rules', post(admin, { csrf: 'csrf-token', kind: 'domain', value: '<evil>', action: 'block', version: version(null, secret) }));
  assert.equal(res.status, 400);
  res = await request('/sync', post(admin, { csrf: 'csrf-token' }));
  assert.equal(res.status, 202); assert.match(await res.text(), /has not completed/); assert.equal(builds.length, 1);
});

test('normalizes and validates domain, IP, CIDR, ASN', () => {
  assert.equal(ruleKey('domain', 'ExAmple.COM.'), 'example.com');
  assert.equal(ruleKey('domain', '.io'), 'io');
  assert.equal(ruleKey('ip', '192.0.2.129/24'), 'IP#192.0.2.0/24');
  assert.equal(ruleKey('ip', '2001:db8::1/64'), 'IP#2001:db8::/64');
  assert.equal(ruleKey('asn', 'AS00042'), 'AS#42');
  assert.throws(() => ruleKey('ip', '192.0.2.1/33'));
  assert.throws(() => ruleKey('asn', '4294967296'));
  assert.throws(() => ruleKey('domain', '-bad.example'));
  assert.throws(() => ruleKey('ip', 'fe80::1%eth0'));
  assert.throws(() => ruleKey('ip', '::ffff:192.0.2.1'));
  assert.equal(ruleAfter({ redirect: '192.0.2.1' }, 'example.com', 'block').redirect, undefined);
  assert.equal(ruleAfter(undefined, 'example.com', 'redirect', '192.0.2.1').redirect, '192.0.2.1');
});

test('expired authentication and oversized or duplicate forms are rejected', async () => {
  const expired = await sealData({ sub: 'user', role: 'admin', csrf: 'csrf-token', exp: 1 }, { password: secret, ttl: 28800 });
  assert.equal((await request('/rules', { headers: { cookie: `__Host-dns-session=${expired}` } })).status, 401);
  const admin = await session();
  assert.equal((await request('/sync', { method: 'POST', headers: { cookie: admin, 'content-type': 'application/x-www-form-urlencoded' },
    body: 'csrf=csrf-token&csrf=csrf-token' })).status, 400);
  assert.equal((await request('/sync', { method: 'POST', headers: { cookie: admin, 'content-type': 'application/x-www-form-urlencoded' },
    body: 'csrf=csrf-token&value=' + 'a'.repeat(17000) })).status, 400);
  assert.equal((await request('/sync', post(admin, { csrf: 'csrf-token', action: 'rollback', source: 'unapproved', commit: 'a'.repeat(40) }))).status, 400);
});

test('SDK transaction preserves ASN index and removes managed attributes atomically with audit', async () => {
  const commands = [];
  const fake = { send: async cmd => { commands.push(cmd); return {}; } };
  const service = services(config, { db: fake, secrets: fake, builds: fake });
  const before = { PK: 'AS#42', SK: 'AS#42', blocked: true, managedBy: 'blocklist-sync', sourceLists: new Set(['feed']), ManagedPK: 'BLOCKLIST_SYNC#0', ManagedSK: 'AS#42' };
  await service.change('AS#42', before, 'block', 'actor');
  const tx = commands[0].input.TransactItems;
  assert.equal(tx.length, 2);
  assert.equal(tx[0].Put.Item.GSI1PK, 'BLOCKED_ASNS'); assert.equal(tx[0].Put.Item.GSI1SK, '42'); assert.equal(tx[0].Put.Item.asn, 42);
  assert.equal(tx[0].Put.Item.managedBy, undefined); assert.equal(tx[0].Put.Item.sourceLists, undefined);
  assert.match(tx[0].Put.ConditionExpression, /attribute_exists\(PK\)/);
  assert.equal(tx[1].Put.Item.PK, 'AUDIT'); assert.equal(tx[1].Put.Item.before.managedBy, 'blocklist-sync');
  await service.change('AS#42', tx[0].Put.Item, 'allow', 'actor');
  assert.equal(commands[1].input.TransactItems[0].Put.Item.allowed, true);
  assert.equal(commands[1].input.TransactItems[0].Put.Item.GSI1PK, undefined);
});
