import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import crypto from 'node:crypto';
import {sanitize} from '../extension/sanitize.mjs';

test('only supported usage and reset fields cross the native boundary', () => {
  const result = sanitize({ok: true, usage: {five_hour: {utilization: 12, resets_at: '2026-09-28T01:00:00Z', cookie: 'secret'}, sessionKey: 'secret', organizationId: 'private', model_scoped: [{name: 'Fable', utilization: 50}]}});
  assert.deepEqual(result, {ok: true, usage: {five_hour: {utilization: 12, resets_at: '2026-09-28T01:00:00.000Z'}, fable: {utilization: 50, resets_at: null}}});
  assert.equal(JSON.stringify(result).includes('secret'), false);
});
test('malformed percentages and unlabeled weekly usage are not accepted', () => {
  for (const utilization of [-1, 101, '20', NaN, Infinity]) assert.equal(sanitize({ok:true, usage:{five_hour:{utilization}, seven_day:{utilization: 30}}}).ok, false);
  assert.equal(sanitize({ok:true, usage:{five_hour:{utilization:0.5}}}).usage.five_hour.utilization, 0.5);
  assert.equal(sanitize({ok:false, kind:'secret', status:403, cookie:'secret'}).kind, 'network');
});
test('extension identity and permissions match the native host', () => {
  const manifest = JSON.parse(fs.readFileSync(new URL('../extension/manifest.json', import.meta.url)));
  const id = [...crypto.createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0,16)].map(b => String.fromCharCode(97+(b>>4),97+(b&15))).join('');
  const host = JSON.parse(fs.readFileSync(new URL('../ClaudeUsageWidget.NativeHost/com.claude_usage_widget.bridge.json', import.meta.url)));
  assert.deepEqual(host.allowed_origins, [`chrome-extension://${id}/`]);
  assert.ok(fs.readFileSync(new URL('../ClaudeUsageWidget.Core/NativeProtocol.cs', import.meta.url), 'utf8').includes(`ExtensionId = "${id}"`));
  assert.deepEqual(manifest.host_permissions, ['https://claude.ai/*']);
  assert.equal(manifest.permissions.some(p => ['debugger', 'cookies', '<all_urls>'].includes(p)), false);
});
const worker = fs.readFileSync(new URL('../extension/background.js', import.meta.url), 'utf8').replace("import {sanitize} from './sanitize.mjs';", '');
function harness(initial = {}) {
  let handler, popup;
  const state = {...initial}; const responses = []; let calls = 0; let pending = null;
  let result = {ok: true, usage: {five_hour: {utilization: 20}}};
  const chrome = {
    runtime: {id:'test', getURL:p=>'chrome-extension://test/'+p, onMessage:{addListener:f=>popup=f}, connectNative:()=>({onDisconnect:{addListener:()=>{}}, onMessage:{addListener:f=>handler=f}, postMessage:m=>responses.push(m)})},
    storage: {session:{get:async keys=>typeof keys === 'string' ? {[keys]:state[keys]} : Object.fromEntries(keys.map(k=>[k,state[k]])), set:async values=>Object.assign(state, values)}},
    tabs:{query:async()=>[{id:7,url:'https://claude.ai/settings/usage'}], get:async id=>({id,url:'https://claude.ai/settings/usage'})},
    scripting:{executeScript:async()=>{calls++;if(pending) await pending;return [{frameId:0,result}];}},
    alarms:{onAlarm:{addListener:()=>{}},create:()=>{}}
  };
  vm.runInNewContext(worker, {chrome,sanitize,Date,setTimeout,clearTimeout});
  return {
    select:()=>new Promise(resolve=>popup({type:'select'}, {id:'test',url:'chrome-extension://test/popup.html'},resolve)),
    fetch:async()=>{await handler({type:'fetch',id:'a'.repeat(32)});return responses.at(-1);},
    setResult:value=>result=value, get calls(){return calls;}, state
    , hold:()=>{let release;pending=new Promise(resolve=>release=resolve);return()=>{pending=null;release();};}
  };
}
test('403 stops subsequent page requests until explicit resume', async () => {
  const h=harness(); await h.select(); h.setResult({ok:false,kind:'http',status:403});
  assert.equal((await h.fetch()).kind,'challenge'); await h.fetch(); assert.equal(h.calls,1);
  await h.select(); h.setResult({ok:true,usage:{five_hour:{utilization:30}}});
  assert.equal((await h.fetch()).usage.five_hour.utilization,30); assert.equal(h.calls,2);
});
test('429 cooldown survives user reselection', async () => {
  const h=harness(); await h.select(); h.setResult({ok:false,kind:'http',status:429,retryAfter:300});
  await h.fetch(); await h.select(); assert.equal((await h.fetch()).status,429); assert.equal(h.calls,1);
});
test('overlapping fetch receives an immediate busy reply without a second page request', async () => {
  const h=harness(); await h.select(); const release=h.hold();
  const first=h.fetch(); await Promise.resolve(); await Promise.resolve();
  assert.equal((await h.fetch()).kind,'busy'); assert.equal(h.calls,1);
  release(); assert.equal((await first).ok,true);
});
const usageScript = fs.readFileSync(new URL('../extension/usage.js', import.meta.url),'utf8');
test('challenge page is detected before making any API requests', async () => {
  let calls=0;
  const result=await vm.runInNewContext(usageScript,{AbortController,setTimeout,clearTimeout,location:{origin:'https://claude.ai',pathname:'/'},document:{title:'Just a moment...',querySelector:()=>null,cookie:''},fetch:()=>{calls++;}});
  assert.equal(result.kind,'challenge'); assert.equal(calls,0);
});
test('login page is not polled for usage', async () => {
  const result=await vm.runInNewContext(usageScript,{AbortController,setTimeout,clearTimeout,location:{origin:'https://claude.ai',pathname:'/login'},document:{}});
  assert.equal(result.kind,'login');
});
test('server challenge header pauses even when HTTP is successful', async () => {
  const result=await vm.runInNewContext(usageScript,{AbortController,setTimeout,clearTimeout,location:{origin:'https://claude.ai',pathname:'/settings/usage'},document:{title:'Claude',querySelector:()=>null,cookie:'lastActiveOrg=test-org'},fetch:async()=>({status:200,ok:true,headers:{get:key=>key==='cf-mitigated'?'challenge':null}})});
  assert.equal(result.kind,'challenge');
});
