import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../rules.js', import.meta.url), 'utf8');
function fixture(path = '/domains') {
  const events = {}, timers = new Map(), requests = [], urls = [];
  let nextTimer = 0;
  const input = { value: 'example.com', addEventListener: (name, fn) => { events[name] = fn; } };
  const kind = { value: 'domain', addEventListener: (name, fn) => { events[name] = fn; } };
  const form = {
    getAttribute: () => path,
    elements: { value: input, kind }, reportValidity: () => input.value !== '',
    addEventListener: (name, fn) => { events[name] = fn; }
  };
  const attributes = new Map();
  const results = {
    innerHTML: 'original', disabled: false,
    setAttribute: (name, value) => attributes.set(name, value),
    removeAttribute: name => attributes.delete(name)
  };
  const status = { textContent: '', replaceChildren: link => { status.link = link; } };
  vm.runInNewContext(source, {
    document: {
      querySelector: selector => ({ '#rule-lookup': form, '#rule-results': results, '#lookup-status': status })[selector],
      createElement: () => ({})
    },
    FormData: class { constructor() { return [['value', input.value]]; } },
    URLSearchParams, AbortController, console,
    history: { replaceState: (_, __, url) => urls.push(url) },
    setTimeout: (callback, delay) => { assert.equal(delay, 400); timers.set(++nextTimer, callback); return nextTimer; },
    clearTimeout: id => timers.delete(id),
    fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }))
  });
  return {
    input, kind, events, results, status, requests, urls, timers, attributes,
    tick: () => { const [id, fn] = timers.entries().next().value; timers.delete(id); return fn(); }
  };
}
const response = (html, status = 200) => ({ ok: status === 200, status, text: async () => html });

test('typing is debounced, keeps the input intact and disables previous actions', async () => {
  const f = fixture();
  f.input.value = 'missing';
  f.events.input();
  f.input.value = 'missing.example';
  f.events.input();
  assert.equal(f.timers.size, 1);
  assert.equal(f.results.disabled, true);
  const loading = f.tick();
  assert.equal(f.requests.length, 1);
  assert.match(f.requests[0].url, /value=missing.example&fragment=1/);
  f.requests[0].resolve(response('<p>No rule exists</p>'));
  await loading;
  assert.equal(f.input.value, 'missing.example');
  assert.equal(f.results.innerHTML, '<p>No rule exists</p>');
  assert.equal(f.results.disabled, false);
  assert.equal(f.urls[0], '/domains?value=missing.example');
});

test('older responses cannot overwrite a newer lookup even if cancellation arrives late', async () => {
  const f = fixture();
  f.events.input();
  const first = f.tick();
  f.input.value = 'latest.example';
  f.events.input();
  assert.equal(f.requests[0].options.signal.aborted, true);
  const second = f.tick();
  f.requests[1].resolve(response('latest'));
  await second;
  f.requests[0].resolve(response('old'));
  await first;
  assert.equal(f.results.innerHTML, 'latest');
  assert.equal(f.results.disabled, false);
});

test('lookup errors preserve typed values, disable stale controls and permit retry', async () => {
  const f = fixture();
  for (const status of [400, 503, 401]) {
    f.events.input();
    const loading = f.tick();
    f.requests.at(-1).resolve(response('', status));
    await loading;
    assert.equal(f.results.innerHTML, 'original');
    assert.equal(f.results.disabled, true);
    assert.equal(f.input.value, 'example.com');
    assert.equal(f.attributes.has('aria-busy'), false);
  }
  assert.match(f.status.link.href, /^\/login\?returnTo=/);
  let prevented = false;
  f.events.submit({ preventDefault: () => { prevented = true; } });
  f.requests.at(-1).resolve(response('recovered'));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(prevented, true);
  assert.equal(f.results.disabled, false);
  assert.equal(f.results.innerHTML, 'recovered');
});

test('clearing the input never triggers a browse scan and lookup stays on the ASN page', async () => {
  const f = fixture('/asns');
  f.input.value = '';
  f.events.input();
  assert.equal(f.timers.size, 0);
  assert.equal(f.results.disabled, true);
  assert.equal(f.requests.length, 0);
  f.input.value = '42';
  f.events.input();
  const loading = f.tick();
  assert.equal(f.requests[0].url, '/asns?value=42&fragment=1');
  f.requests[0].resolve(response('ASN'));
  await loading;
});
