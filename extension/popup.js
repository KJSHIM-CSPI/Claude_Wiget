const status = document.getElementById('status');
async function send(type) {
  try { status.textContent = (await chrome.runtime.sendMessage({type})).text; }
  catch { status.textContent = '확장 프로그램을 다시 열어 주세요.'; }
}
document.getElementById('connect').addEventListener('click', () => send('select'));
send('status');
