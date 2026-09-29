const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../extension/usage.js'), 'utf8');

async function run({ organizations = [{ uuid: 'org-1' }], cookie = '', origin = 'https://claude.ai', status = 200, retry = null, usage = { five_hour: { utilization: 25 } }, failNetwork = false, invalidJson = false } = {}) {
  const calls = [];
  const context = {
    location: { origin, pathname: '/settings/usage' }, document: { cookie, title: 'Claude', querySelector: () => null }, AbortController, setTimeout, clearTimeout,
    fetch: async (url, options) => {
      calls.push({ url, options });
      if (failNetwork) throw new Error('network failed');
      return { ok: status === 200, status, headers: { get: key => key === 'retry-after' ? retry : null }, json: async () => {
        if (invalidJson) throw new SyntaxError('not JSON');
        return url === '/api/organizations' ? organizations : usage;
      } };
    }
  };
  const result = JSON.parse(JSON.stringify(await vm.runInNewContext(source, context)));
  return { calls, result };
}

test('fetches only same-origin usage and passes credentials inside the browser', async () => {
  const { calls, result } = await run();
  assert.equal(result.ok, true);
  assert.equal(calls[1].url, '/api/organizations/org-1/usage');
  assert.equal(calls[1].options.credentials, 'same-origin');
  assert.equal(calls[1].options.cache, 'no-store');
  assert.equal(result.usage.five_hour.utilization, 25);
});
test('selects active org instead of the first org', async () => {
  const { calls } = await run({ organizations: [{ uuid: 'org-1' }, { uuid: 'org-2' }], cookie: 'other=value; lastActiveOrg=org-2' });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, '/api/organizations/org-2/usage');
});
test('ambiguous organization requires explicit browser selection', async () => {
  const { calls, result } = await run({ organizations: [{ uuid: 'org-1' }, { uuid: 'org-2' }] });
  assert.equal(result.status, 409);
  assert.equal(calls.length, 1);
});
test('signed out response does not invent zero usage', async () => {
  const { result } = await run({ status: 401 });
  assert.equal(result.ok, false);
  assert.equal(result.status, 401);
  assert.equal(result.kind, 'http');
  assert.equal(result.usage, undefined);
});
test('foreign login origin is never queried', async () => {
  const { calls, result } = await run({ origin: 'https://accounts.google.com' });
  assert.equal(calls.length, 0);
  assert.equal(result.status, 0);
  assert.equal(result.kind, 'wrong_origin');
});
test('rate-limit delay is forwarded', async () => {
  const { result } = await run({ status: 429, retry: '120' });
  assert.equal(result.status, 429);
  assert.equal(result.retryAfter, 120);
});
test('network failures finish the request with an explicit failure', async () => {
  const { result } = await run({ failNetwork: true });
  assert.equal(result.ok, false);
  assert.equal(result.status, 0);
});
test('malformed org identifiers cannot form a different endpoint', async () => {
  const { calls, result } = await run({ organizations: [{ uuid: '../other?secret' }] });
  assert.equal(calls.length, 1);
  assert.equal(result.status, 422);
});

test('unexpected successful organization payload is not called a login failure', async () => {
  const { result } = await run({ organizations: { unexpected: [] } });
  assert.equal(result.status, 200);
  assert.equal(result.kind, 'unexpected_shape');
  assert.equal(result.stage, 'organizations');
});
test('empty organization list is distinguished from rejected authentication', async () => {
  const { result } = await run({ organizations: [] });
  assert.equal(result.status, 200);
  assert.equal(result.kind, 'no_organizations');
});
test('wrapped organization list can be consumed', async () => {
  const { result } = await run({ organizations: { organizations: [{ uuid: 'org-1' }] } });
  assert.equal(result.ok, true);
});
test('HTML security challenges are distinguished from expired sessions', async () => {
  const { result } = await run({ invalidJson: true });
  assert.equal(result.kind, 'invalid_json');
  assert.equal(result.status, 200);
});
test('malformed active-org cookie falls back without throwing', async () => {
  const { result } = await run({ cookie: 'lastActiveOrg=%broken' });
  assert.equal(result.ok, true);
});
test('active-org cannot inject a different endpoint', async () => {
  const { calls, result } = await run({ cookie: 'lastActiveOrg=..%2Fother%3Fsecret' });
  assert.equal(result.ok, true);
  assert.equal(calls[1].url, '/api/organizations/org-1/usage');
});
test('usage authentication failure preserves the failing stage', async () => {
  const { result } = await run({ cookie: 'lastActiveOrg=org-1', status: 401 });
  assert.equal(result.stage, 'usage');
  assert.equal(result.status, 401);
});
