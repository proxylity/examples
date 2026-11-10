import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { sealData, unsealData } from 'iron-session';
import { kindOf, ruleKey, version, sameVersion } from './policy.js';

const SESSION = '__Host-dns-session', TRANSACTION = '__Host-dns-login';
const sessionTtl = 8 * 60 * 60, loginTtl = 10 * 60;
const escape = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const json = value => JSON.stringify(value, (k, v) => v instanceof Set ? [...v].sort() : v, 2);
const cookie = (name, value, maxAge) => `${name}=${value}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAge}`;
const token = () => randomBytes(32).toString('base64url');
const rulesScript = readFileSync(new URL('./rules.js', import.meta.url), 'utf8');
const rulePages = {
  domain: { path: '/domains', title: 'Domains', label: 'Domain', placeholder: 'example.com' },
  ip: { path: '/ip-cidrs', title: 'IP/CIDRs', label: 'IP/CIDR', placeholder: '192.0.2.0/24' },
  asn: { path: '/asns', title: 'ASNs', label: 'ASN', placeholder: 'AS42' }
};
const pageKind = path => Object.keys(rulePages).find(kind => rulePages[kind].path === path);
const loginPaths = ['/', '/rules', '/sync', '/audit', ...Object.values(rulePages).map(p => p.path)];
const details = value => `<details><summary>Details</summary><pre>${escape(json(value))}</pre></details>`;
const fields = entries => `<dl>${entries.map(([label, value]) => `<dt>${escape(label)}</dt><dd>${escape(value ?? 'Not reported')}</dd>`).join('')}</dl>`;
function syncSummary(state, lease) {
  const last = Number(state?.lastSuccess);
  return fields([['Status', state?.status], ['Last successful sync', last > 0 ? new Date(last * 1000).toISOString() : null],
    ['Worker lease', lease ? 'Held' : 'Not held'], ...(state?.error ? [['Error', state.error]] : [])]) +
    details({ state: state || null, lease: lease || null });
}
function actions() {
  return [['block', '⛔', 'Block'], ['allow', '✓', 'Allow']].map(([value, icon, label]) => iconButton(value, icon, label)).join('');
}
function iconButton(value, icon, label) {
  return `<button name="action" value="${value}" aria-label="${label}" title="${label}"><span aria-hidden="true">${icon}</span></button>`;
}
function ruleForm(item, kind, value, session, secret) {
  if (session.role !== 'admin') return '';
  return `<form method="post" action="${rulePages[kind].path}">${formCsrf(session)}<input type="hidden" name="kind" value="${kind}"><input type="hidden" name="value" value="${escape(value)}"><input type="hidden" name="version" value="${version(item, secret)}">${actions()}<label>Redirect IP <input name="redirect" value="${escape(item?.redirect || '')}" placeholder="192.0.2.1"></label>${iconButton('redirect', '↪', 'Redirect')}${item && item.managedBy !== 'blocklist-sync' ? iconButton('delete', '🗑', 'Delete override') : ''}</form>`;
}
function ruleResults(result, kind, value, session, secret, exact) {
  const rows = (result.Items || []).filter(i => i.PK === i.SK && kindOf(i.PK) === kind);
  const rules = rows.map(i => `<article><h2>${escape(i.PK)}</h2><p>${escape(i.allowed === true ? 'Allowed' : i.blocked === true ? 'Blocked' : i.redirect ? `Redirect to ${i.redirect}` : 'No action')} · ${i.managedBy === 'blocklist-sync' ? 'Managed' : 'Customer'}</p>${ruleForm(i, kindOf(i.PK), i.PK.replace(/^(IP#|AS#)/, ''), session, secret)}${details(i)}</article>`).join('');
  const missing = exact && rows.length === 0;
  return `${missing ? `<p>No rule exists for <strong>${escape(value)}</strong>.</p>${session.role === 'admin' ? `<h2>Create a rule</h2>${ruleForm(null, kind, value, session, secret)}` : ''}` : rules}${!exact && rows.length === 0 ? '<p>No rules of this type on this page.</p>' : ''}${nextLink(rulePages[kind].path, result.LastEvaluatedKey)}`;
}

function readCookie(req, name) {
  const found = (req.headers.cookie || '').split(';').map(x => x.trim()).filter(x => x.startsWith(`${name}=`));
  return found.length === 1 ? found[0].slice(name.length + 1) : '';
}
async function unseal(value, secret, ttl) {
  try { return value ? await unsealData(value, { password: secret, ttl }) : null; }
  catch (error) { console.warn('Invalid authentication cookie:', error.name); return null; }
}
function cursorFrom(url) {
  const text = url.searchParams.get('cursor');
  if (!text) return undefined;
  if (text.length > 2048 || !/^[A-Za-z0-9_-]+$/.test(text)) throw Error('Invalid cursor');
  const key = JSON.parse(Buffer.from(text, 'base64url').toString('utf8'));
  if (!key || Object.keys(key).sort().join(',') !== 'PK,SK' || typeof key.PK !== 'string' || typeof key.SK !== 'string' || key.PK.length > 1024 || key.SK.length > 1024) throw Error('Invalid cursor');
  return key;
}
function nextLink(path, key) {
  return key ? `<p><a href="${path}?cursor=${Buffer.from(JSON.stringify(key)).toString('base64url')}">Next page</a></p>` : '';
}
function page(title, content, user) {
  const isRules = Object.values(rulePages).some(p => p.title === title);
  const navigation = `<nav><a href="/">Overview</a>${Object.values(rulePages).map(p => `<a href="${p.path}"${p.title === title ? ' aria-current="page"' : ''}>${p.title}</a>`).join('')}<a href="/sync">Sync</a><a href="/audit">Audit</a></nav>`;
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${escape(title)} - DNS admin</title><style>body{font:1rem system-ui;max-width:60rem;margin:2rem auto;padding:0 1rem;overflow-wrap:anywhere}nav{display:flex;flex-wrap:wrap;gap:1rem;margin-bottom:2rem}input,select,button{font:inherit;margin:.25rem;max-width:100%;box-sizing:border-box;height:2.75rem;padding:.5rem .75rem}select{padding-right:2rem}button{cursor:pointer}form{display:flex;flex-wrap:wrap;align-items:center;gap:.25rem}label{display:inline-flex;flex-wrap:wrap;align-items:center}article{border-top:1px solid #ccc;padding:1rem 0}article h2{font-size:1.1rem}pre{white-space:pre-wrap;overflow-wrap:anywhere}summary{cursor:pointer;padding:.5rem 0}dt{font-weight:600}dd{margin:0 0 1rem}fieldset{border:0;padding:0;margin:0;min-width:0}#lookup-status{min-height:1.5rem}</style>${isRules ? '<script src="/rules.js" defer></script>' : ''}</head><body><h1>${escape(title)}</h1>${user ? navigation : ''}<main>${content}</main></body></html>`;
}
function formCsrf(session) { return `<input type="hidden" name="csrf" value="${escape(session.csrf)}">`; }
function write(res, status, body, extra = {}) {
  res.writeHead(status, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'content-security-policy': "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'", 'x-content-type-options': 'nosniff', 'referrer-policy': 'no-referrer', 'x-frame-options': 'DENY', 'strict-transport-security': 'max-age=31536000', ...extra });
  res.end(body);
}
function redirect(res, path, headers = {}) { write(res, 303, '', { location: path, ...headers }); }
function returnPath(value, origin) {
  if (!value) return '/';
  if (value.length > 2048 || !value.startsWith('/') || value.startsWith('//')) throw Error('Invalid login destination');
  const url = new URL(value, origin);
  if (url.origin !== origin || url.hash || !loginPaths.includes(url.pathname)) throw Error('Invalid login destination');
  return url.pathname + url.search;
}
async function bodyParams(req) {
  if ((req.headers['content-type'] || '').split(';')[0].trim().toLowerCase() !== 'application/x-www-form-urlencoded') throw Error('Invalid content type');
  let size = 0, chunks = [];
  for await (const chunk of req) { size += chunk.length; if (size > 16384) throw Error('Form too large'); chunks.push(chunk); }
  const params = new URLSearchParams(Buffer.concat(chunks).toString('utf8'));
  for (const k of new Set(params.keys())) if (params.getAll(k).length !== 1) throw Error('Duplicate field');
  return params;
}

export function configFromEnv(env) {
  const names = ['TABLE_NAME', 'HOME_REGION', 'BUILD_PROJECT', 'BLOCKLIST_PROFILE', 'COGNITO_ISSUER', 'COGNITO_CLIENT_ID', 'COGNITO_DOMAIN', 'SESSION_SECRET_ARN', 'PUBLIC_URL'];
  for (const name of names) if (!env[name]) throw Error(`Missing ${name}`);
  const c = Object.fromEntries(names.map(k => [k, env[k]]));
  for (const name of ['PUBLIC_URL', 'COGNITO_ISSUER', 'COGNITO_DOMAIN']) {
    const url = new URL(c[name]);
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) throw Error(`Invalid ${name}`);
    if ((name === 'PUBLIC_URL' || name === 'COGNITO_DOMAIN') && url.pathname !== '/') throw Error(`${name} must be an origin`);
    c[name] = url.href.replace(/\/$/, '');
  }
  return c;
}

export function createApp(config, svc) {
  return async (req, res) => {
    try {
      const url = new URL(req.url, config.PUBLIC_URL);
      if (!req.url.startsWith('/') || req.url.startsWith('//') || url.origin !== config.PUBLIC_URL) return write(res, 400, page('Bad request', '', null));
      const secret = await svc.secret();
      const session = await unseal(readCookie(req, SESSION), secret, sessionTtl);
      const now = Math.floor(Date.now() / 1000);
      const valid = session && session.exp > now && typeof session.csrf === 'string' && typeof session.sub === 'string' && ['admin', 'viewer'].includes(session.role);
      if (req.method === 'GET' && url.pathname === '/login') {
        let destination;
        try { destination = returnPath(url.searchParams.get('returnTo'), config.PUBLIC_URL); }
        catch (error) { return write(res, 400, page('Invalid login destination', escape(error.message), null)); }
        const { url: loginUrl, transaction } = await svc.loginUrl(`${config.PUBLIC_URL}/callback`);
        transaction.returnTo = destination;
        const sealed = await sealData(transaction, { password: secret, ttl: loginTtl });
        return redirect(res, loginUrl, { 'set-cookie': cookie(TRANSACTION, sealed, loginTtl) });
      }
      if (req.method === 'GET' && url.pathname === '/callback') {
        const transaction = await unseal(readCookie(req, TRANSACTION), secret, loginTtl);
        if (!transaction || !transaction.verifier || !transaction.state || !transaction.nonce) return write(res, 400, page('Invalid login', 'Please start again at <a href="/login">login</a>.', null), { 'set-cookie': cookie(TRANSACTION, '', 0) });
        let claims;
        try { claims = await svc.callback(url, transaction); } catch (error) { console.warn('Login verification failed:', error.name); return write(res, 400, page('Invalid login', 'Login could not be verified.', null), { 'set-cookie': cookie(TRANSACTION, '', 0) }); }
        const groups = Array.isArray(claims?.['cognito:groups']) ? claims['cognito:groups'] : [];
        const role = groups.includes('admin') ? 'admin' : groups.includes('viewer') ? 'viewer' : null;
        if (!role || typeof claims?.sub !== 'string' || !Number.isFinite(claims.exp) || claims.exp <= now) return write(res, 403, page('Access denied', 'Membership in viewer or admin is required.', null), { 'set-cookie': cookie(TRANSACTION, '', 0) });
        const newSession = { sub: claims.sub, role, csrf: token(), exp: Math.min(now + sessionTtl, claims.exp) };
        const sealed = await sealData(newSession, { password: secret, ttl: sessionTtl });
        return redirect(res, returnPath(transaction.returnTo, config.PUBLIC_URL), { 'set-cookie': [cookie(TRANSACTION, '', 0), cookie(SESSION, sealed, newSession.exp - now)] });
      }
      if (!valid) {
        const destination = loginPaths.includes(url.pathname) ? url.pathname + url.search : '/';
        return write(res, 401, page('Login required', `<a href="/login?returnTo=${escape(encodeURIComponent(destination))}">Login</a>`, null));
      }
      if (req.method === 'POST') {
        let form;
        try { form = await bodyParams(req); }
        catch (error) { return write(res, 400, page('Invalid form', escape(error.message), session)); }
        if (!sameVersion(form.get('csrf'), session.csrf)) return write(res, 403, page('Invalid request', 'CSRF check failed.', session));
        if (url.pathname === '/logout') return redirect(res, '/login', { 'set-cookie': cookie(SESSION, '', 0) });
        if (session.role !== 'admin') return write(res, 403, page('Access denied', 'Administrator role required.', session));
        if (url.pathname === '/rules' || pageKind(url.pathname)) {
          let key;
          const kind = pageKind(url.pathname) || form.get('kind');
          if (pageKind(url.pathname) && form.get('kind') !== kind) return write(res, 400, page('Invalid rule', 'Rule type does not match this page.', session));
          try { key = ruleKey(kind, form.get('value')); } catch (e) { return write(res, 400, page('Invalid rule', escape(e.message), session)); }
          const action = form.get('action');
          if (!['block', 'allow', 'delete', 'redirect'].includes(action)) return write(res, 400, page('Invalid action', '', session));
          let redirectIp;
          if (action === 'redirect') {
            try {
              const value = form.get('redirect');
              if (!value || value.includes('/')) throw Error('Redirect requires a single IP address');
              redirectIp = ruleKey('ip', value).slice(3).split('/')[0];
            } catch (error) { return write(res, 400, page('Invalid redirect', escape(error.message), session)); }
          }
          const before = await svc.get(key);
          if (!sameVersion(form.get('version'), version(before, secret))) return write(res, 409, page('Conflict', 'Rule changed. Reload and retry.', session));
          if (action === 'delete' && (!before || before.managedBy === 'blocklist-sync')) return write(res, 400, page('Invalid action', 'Only customer-owned rules can be deleted. Take ownership first.', session));
          try { await svc.change(key, before, action, session.sub, redirectIp); }
          catch (e) { if (['TransactionCanceledException', 'ConditionalCheckFailedException'].includes(e.name)) return write(res, 409, page('Conflict', 'Rule changed or the transaction could not be committed. Reload and retry.', session)); throw e; }
          return redirect(res, `${rulePages[kind].path}?${new URLSearchParams({ value: form.get('value') })}`);
        }
        if (url.pathname === '/sync') {
          let rollback;
          if (form.get('action') === 'rollback') {
            const source = form.get('source'), commit = form.get('commit');
            const hagezi = { Light: 'hagezi:light', Balanced: 'hagezi:normal', Strict: 'hagezi:pro' }[config.BLOCKLIST_PROFILE];
            if (![hagezi, 'firehol:spamhaus-drop'].includes(source) || !/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/i.test(commit || ''))
              return write(res, 400, page('Invalid rollback', 'An enabled list ID and full cached commit SHA are required.', session));
            rollback = { source, commit };
          }
          const result = await svc.startBuild(session.sub, rollback);
          return write(res, 202, page('Sync accepted', `<p>StartBuild accepted; it has not completed.</p>${fields([['Build', result.build?.id], ['Status', result.build?.buildStatus]])}${details(result)}<a href="/sync">Sync status</a>`, session));
        }
        return write(res, 404, page('Not found', '', session));
      }
      if (req.method !== 'GET' && req.method !== 'HEAD') return write(res, 405, page('Method not allowed', '', session));
      if (url.pathname === '/rules.js') return write(res, 200, rulesScript, { 'content-type': 'text/javascript; charset=utf-8' });
      const logout = `<form method="post" action="/logout">${formCsrf(session)}<button>Sign out locally</button></form>`;
      if (url.pathname === '/') {
        const state = await svc.get('SYNC#STATE');
        const lease = await svc.get('SYNC#LEASE');
        return write(res, 200, page('Overview', `<p>Profile: ${escape(config.BLOCKLIST_PROFILE)}</p><p>Signed in as ${escape(session.sub)} (${escape(session.role)}).</p>${syncSummary(state, lease)}${logout}`, session));
      }
      if (url.pathname === '/rules' || pageKind(url.pathname)) {
        let cursor;
        try { cursor = cursorFrom(url); } catch { return write(res, 400, page('Invalid cursor', '', session)); }
        let result, prefill = '';
        const kind = pageKind(url.pathname) || url.searchParams.get('kind') || 'domain';
        const selected = rulePages[kind];
        if (!selected) return write(res, 400, page('Invalid rule', 'Invalid rule type.', session));
        if (url.searchParams.has('value')) {
          let key;
          try { key = ruleKey(kind, url.searchParams.get('value')); }
          catch (error) { return write(res, 400, page('Invalid rule', escape(error.message), session)); }
          const item = await svc.get(key);
          prefill = url.searchParams.get('value');
          result = { Items: item ? [item] : [] };
        } else result = await svc.rules(cursor, kind);
        const exact = url.searchParams.has('value');
        const content = ruleResults(result, kind, prefill, session, secret, exact);
        if (url.searchParams.get('fragment') === '1') return write(res, 200, content);
        const lookup = `<form id="rule-lookup" method="get" action="${selected.path}"><label>${selected.label} <input name="value" value="${escape(prefill)}" required maxlength="253" autocomplete="off" spellcheck="false" placeholder="${selected.placeholder}"></label><button>Load rule</button></form><p id="lookup-status" role="status" aria-live="polite"></p>`;
        return write(res, 200, page(selected.title, `${lookup}<fieldset id="rule-results" aria-label="Loaded rules">${content}</fieldset><details><summary>About rules</summary><p>Rules load automatically when you pause typing. Changes create customer overrides. Deleting an override does not restore an unchanged managed policy. Browsing scans up to 50 table items per page and shows only this rule type; empty pages may have a next page. Exact lookups use one keyed read.</p></details>`, session));
      }
      if (url.pathname === '/audit') {
        let cursor;
        try { cursor = cursorFrom(url); if (cursor && cursor.PK !== 'AUDIT') throw Error(); } catch { return write(res, 400, page('Invalid cursor', '', session)); }
        const result = await svc.audit(cursor);
        return write(res, 200, page('Audit', `${(result.Items || []).map(i => `<article>${fields([['Time', i.SK?.split('#')[0]], ['Action', i.action], ['Target', i.target], ['Actor', i.actor], ...(i.status ? [['Status', i.status]] : [])])}${details(i)}</article>`).join('') || '<p>No audit entries on this page.</p>'}${nextLink('/audit', result.LastEvaluatedKey)}`, session));
      }
      if (url.pathname === '/sync') {
        const state = await svc.get('SYNC#STATE'), lease = await svc.get('SYNC#LEASE');
        return write(res, 200, page('Sync', `<p>Profile: ${escape(config.BLOCKLIST_PROFILE)}. Starting a build does not mean synchronization has completed.</p>${syncSummary(state, lease)}${session.role === 'admin' ? `<form method="post" action="/sync">${formCsrf(session)}<button>Start sync</button></form><details><summary>Rollback a list</summary><p>Requires a commit still in the local cache. The next scheduled sync supersedes the rollback.</p><form method="post" action="/sync">${formCsrf(session)}<label>List ID <input name="source" required></label><label>Full cached commit SHA <input name="commit" required></label><button name="action" value="rollback">Rollback list</button></form></details>` : ''}`, session));
      }
      return write(res, 404, page('Not found', '', session));
    } catch (e) {
      console.error('Request failed:', e.name);
      if (!res.headersSent) write(res, 500, page('Request failed', 'Please retry or contact an administrator.', null));
      else res.destroy();
    }
  };
}
