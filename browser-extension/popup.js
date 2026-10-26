const status = document.getElementById('status');
const enabled = document.getElementById('enabled'), inject = document.getElementById('inject');
async function refresh() {
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  try {
    const state = await chrome.tabs.sendMessage(tabs[0].id, { action: 'status' });
    status.textContent = !state.enabled ? '未启用 / Disabled' : state.connected ? state.name : state.failed ? '未连接：检查本机安装 / Not connected' : '连接中 / Connecting';
  } catch { status.textContent = '请打开支持的 AI 网页并刷新 / Refresh a supported AI page'; }
}
chrome.storage.local.get('options').then(result => {
  const options = result.options || { enabled: false, inject: true };
  enabled.checked = options.enabled; inject.checked = options.inject; void refresh();
});
for (const input of [enabled, inject]) input.addEventListener('change', async () => {
  await chrome.storage.local.set({ options: { enabled: enabled.checked, inject: inject.checked } }); void refresh();
});
document.getElementById('retry').addEventListener('click', async () => {
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  try { await chrome.tabs.sendMessage(tabs[0].id, { action: 'retry' }); setTimeout(refresh, 500); } catch { void refresh(); }
});
