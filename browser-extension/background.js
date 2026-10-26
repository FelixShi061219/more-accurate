/* Local native messaging only. No external server, API key or transcript storage. */
const HOST = 'com.moreaccurate.indicator';
const providers = {
  'chatgpt.com': 'ChatGPT', 'chat.openai.com': 'ChatGPT',
  'chat.deepseek.com': 'DeepSeek', 'claude.ai': 'Claude',
  'gemini.google.com': 'Gemini',
};
const connections = new Map(), tabQueues = new Map();
function providerFor(url) { try { return providers[new URL(url).hostname] || null; } catch { return null; } }
async function taskIdentity(sender, provider) {
  const tabId = sender.tab.id, key = 'tab:' + tabId;
  const path = new URL(sender.url).pathname.replace(/\/$/, '') || '/';
  const isConversation = /^(?:\/(c|chat|app)\/[A-Za-z0-9_-]+|\/a\/chat\/s\/[A-Za-z0-9_-]+)/.test(path);
  const stored = (await chrome.storage.session.get(key))[key];
  const routes = stored?.provider === provider ? stored.routes || {} : {};
  let identity;
  if (isConversation && routes[path]) identity = { ...routes[path], routes };
  else if (stored?.provider === provider && stored.path == null) identity = { ...stored, path: isConversation ? path : null, routes };
  else identity = { id: 'web-' + crypto.randomUUID().replaceAll('-', ''), provider, path: isConversation ? path : null, routes };
  if (isConversation) routes[path] = { id: identity.id, provider, path };
  if (stored && stored.id !== identity.id && connections.has(stored.id)) {
    await nativeRequest(stored, { action: 'close' });
    connections.get(stored.id)?.port.disconnect(); connections.delete(stored.id);
  }
  await chrome.storage.session.set({ [key]: identity });
  return identity;
}
function nativeRequest(identity, message) {
  let connection = connections.get(identity.id);
  if (!connection) {
    const port = chrome.runtime.connectNative(HOST);
    connection = { port, pending: [], identity };
    connections.set(identity.id, connection);
    port.onMessage.addListener(reply => {
      const item = connection.pending.shift();
      if (item) { clearTimeout(item.timeout); if (item.action === 'open' && reply.ok) connection.opened = reply; item.resolve(reply); }
    });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError?.message || 'Native connection closed';
      connections.delete(identity.id);
      for (const item of connection.pending.splice(0)) { clearTimeout(item.timeout); item.resolve({ ok: false, error }); }
    });
  }
  return new Promise(resolve => {
    const item = { resolve, action: message.action, timeout: setTimeout(() => {
      connection.port.disconnect(); connections.delete(identity.id);
      for (const waiting of connection.pending.splice(0)) { clearTimeout(waiting.timeout); waiting.resolve({ ok: false, error: 'Local app timed out. Click Retry in the extension.' }); }
    }, 3000) };
    connection.pending.push(item);
    try { connection.port.postMessage({ ...message, id: identity.id, provider: identity.provider }); }
    catch (error) { clearTimeout(item.timeout); connection.pending.pop(); resolve({ ok: false, error: String(error) }); }
  });
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (!sender.tab || sender.frameId !== 0) return;
  const provider = providerFor(sender.url);
  if (!provider || !['open', 'begin', 'marker', 'close'].includes(message.action)) return;
  const tabId = sender.tab.id;
  const operation = (tabQueues.get(tabId) || Promise.resolve()).catch(() => {}).then(async () => {
    const identity = await taskIdentity(sender, provider);
    if (message.action === 'close' && !connections.has(identity.id)) return { ok: true };
    if (message.action === 'open' && message.preserve && connections.get(identity.id)?.opened) return connections.get(identity.id).opened;
    const reply = await nativeRequest(identity, { action: message.action, ...(message.action === 'marker' ? { text: String(message.text || '').slice(-16384) } : {}) });
    if (message.action === 'close') { connections.get(identity.id)?.port.disconnect(); connections.delete(identity.id); }
    return reply;
  });
  tabQueues.set(tabId, operation);
  operation.then(respond, error => respond({ ok: false, error: String(error) }));
  return true;
});
chrome.tabs.onRemoved.addListener(async tabId => {
  await tabQueues.get(tabId)?.catch(() => {}); tabQueues.delete(tabId);
  const key = 'tab:' + tabId;
  const identity = (await chrome.storage.session.get(key))[key];
  if (identity) {
    // Only metadata was stored. Close without starting a new host if already disconnected.
    if (connections.has(identity.id)) await nativeRequest(identity, { action: 'close' });
    connections.get(identity.id)?.port.disconnect(); connections.delete(identity.id);
  }
  await chrome.storage.session.remove(key);
});
if (typeof module !== 'undefined') module.exports = { providerFor };
