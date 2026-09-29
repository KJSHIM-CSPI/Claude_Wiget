import {sanitize} from './sanitize.mjs';
let port = null;
let busy = false;
let lastError = '';
let blocked = false;
let blockedKind = 'paused';
let retryUntil = 0;

function connect() {
  if (port) return;
  lastError = '';
  const current = chrome.runtime.connectNative('com.claude_usage_widget.bridge');
  port = current;
  current.onDisconnect.addListener(() => {
    lastError = chrome.runtime.lastError?.message || '위젯 연결이 끊겼습니다.';
    if (port === current) port = null;
  });
  current.onMessage.addListener(async message => {
    if (message?.type !== 'fetch' || !/^[a-f0-9]{32}$/.test(message?.id || '')) return;
    if (busy) { try { current.postMessage({id: message.id, ok: false, kind: 'busy'}); } catch {} return; }
    busy = true;
    let result;
    try {
      if (blocked) result = {ok: false, kind: blockedKind};
      else if (Date.now() < retryUntil) result = {ok: false, kind: 'http', status: 429, retryAfter: Math.ceil((retryUntil - Date.now()) / 1000)};
      else {
        const {tabId} = await chrome.storage.session.get('tabId');
        const tab = Number.isInteger(tabId) ? await chrome.tabs.get(tabId).catch(() => null) : null;
        if (!tab?.url?.startsWith('https://claude.ai/')) result = {ok: false, kind: 'no_tab'};
        else {
          let timer;
          try {
            const replies = await Promise.race([
              chrome.scripting.executeScript({target: {tabId}, files: ['usage.js']}),
              new Promise(resolve => { timer = setTimeout(() => resolve(null), 22000); })
            ]);
            if (replies === null) {
              blocked = true; blockedKind = 'paused';
              await chrome.storage.session.set({blocked, blockedKind});
              result = {ok: false, kind: 'paused'};
            } else result = sanitize(replies.find(r => r.frameId === 0)?.result);
          } finally { clearTimeout(timer); }
        }
      }
      if (result.status === 401 || result.status === 403 || ['challenge', 'login'].includes(result.kind)) {
        blocked = true;
        blockedKind = result.status === 401 || result.kind === 'login' ? 'login' : 'challenge';
        result = {ok: false, kind: blockedKind, status: result.status || 0};
        await chrome.storage.session.set({blocked, blockedKind});
      }
      if (result.status === 429) {
        retryUntil = Math.max(retryUntil, Date.now() + Math.max(30, result.retryAfter || 60) * 1000);
        await chrome.storage.session.set({retryUntil});
      }
    } catch { result = {ok: false, kind: 'network', status: 0}; }
    finally { busy = false; }
    if (port === current) try { current.postMessage({id: message.id, ...result}); } catch { }
  });
}

const ready = chrome.storage.session.get(['blocked', 'blockedKind', 'retryUntil']).then(state => {
  blocked = !!state.blocked; blockedKind = state.blockedKind || 'paused'; retryUntil = state.retryUntil || 0;
});
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('popup.html')) return;
  (async () => {
    await ready;
    if (message.type === 'select') {
      const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
      if (!tab?.url?.startsWith('https://claude.ai/')) return {text: '평소 Chrome에서 Claude 탭을 선택한 뒤 다시 눌러 주세요.'};
      if (busy) return {text: '현재 조회가 끝난 뒤 다시 눌러 주세요.'};
      await chrome.storage.session.set({tabId: tab.id, blocked: false});
      blocked = false; lastError = '';
      connect();
      return {text: '이 탭을 선택했습니다. 위젯의 새로고침을 눌러 주세요.'};
    }
    return {text: blocked ? '조회가 중지되었습니다. Claude 화면에서 확인을 마친 뒤 이 탭 연결을 눌러 주세요.' : lastError || (port ? '위젯 연결을 유지하고 있습니다.' : '위젯을 실행하고 이 탭 연결을 눌러 주세요.')};
  })().then(reply, () => reply({text: '연결 상태를 확인하지 못했습니다.'}));
  return true;
});
chrome.alarms.onAlarm.addListener(async alarm => {
  if (alarm.name !== 'reconnect') return;
  await ready;
  const {tabId} = await chrome.storage.session.get('tabId');
  if (Number.isInteger(tabId)) connect();
});
chrome.alarms.create('reconnect', {periodInMinutes: 0.5});
