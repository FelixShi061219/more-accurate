(() => {
  'use strict';
  const adapters = {
    'chatgpt.com': { editor: '#prompt-textarea', send: 'button[data-testid="send-button"], button[aria-label="Send prompt"]', replies: '[data-message-author-role="assistant"]' },
    'chat.openai.com': { editor: '#prompt-textarea', send: 'button[data-testid="send-button"]', replies: '[data-message-author-role="assistant"]' },
    'claude.ai': { editor: '[contenteditable="true"][role="textbox"], .ProseMirror[contenteditable="true"]', send: 'button[aria-label="Send message"], button[aria-label="发送消息"], button[data-testid="send-button"]', replies: '[data-testid="assistant-turn"], .font-claude-response' },
    'gemini.google.com': { editor: 'rich-textarea [contenteditable="true"]', send: 'button.send-button, button[aria-label="Send message"], button[aria-label="发送消息"]', replies: 'model-response' },
    'chat.deepseek.com': { editor: 'textarea', send: 'button[type="submit"], [role="button"][aria-label="Send"], [role="button"][aria-label="发送"], [data-testid="send-button"]', replies: '.ds-markdown' },
  };
  const adapter = adapters[location.hostname];
  if (!adapter) return;
  let link = null, failed = false, active = false, lastMarker = '', queued = false, route = location.pathname;
  let baseline = new Map(), sending = false;
  function rememberExistingReplies() { baseline = new Map(Array.from(document.querySelectorAll(adapter.replies), node => [node, node.innerText || ''])); }
  let options = { enabled: false, inject: true };
  function enabled() { return options.enabled && !failed; }
  async function send(message) {
    try {
      const result = await chrome.runtime.sendMessage(message);
      if (!result?.ok) { failed = true; link = null; return result; }
      return result;
    } catch (error) { failed = true; link = null; return { ok: false, error: String(error) }; }
  }
  async function connect(preserve = false) {
    if (!options.enabled) return;
    const previous = link; failed = false; link = await send({ action: 'open', preserve });
    if (preserve && link?.ok && previous?.id === link.id) { schedule(); return; }
    if (link?.ok) { active = false; lastMarker = ''; rememberExistingReplies(); schedule(); }
  }
  function editorText(editor) { return editor instanceof HTMLTextAreaElement ? editor.value : editor.innerText; }
  function append(editor, suffix) {
    const text = editorText(editor);
    if (editor instanceof HTMLTextAreaElement) {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(editor, text + suffix);
      editor.dispatchEvent(new Event('input', { bubbles: true }));
    } else {
      editor.focus();
      const selection = window.getSelection(), range = document.createRange();
      range.selectNodeContents(editor); range.collapse(false); selection.removeAllRanges(); selection.addRange(range);
      if (!document.execCommand('insertText', false, suffix)) throw new Error('Editor does not accept the scoring instruction.');
    }
  }
  function injectBeforeSend(editor) {
    if (!enabled() || !link?.ok || !editorText(editor).trim() || sending) return;
    rememberExistingReplies(); lastMarker = '';
    if (!options.inject || editorText(editor).includes('MA:' + link.nonce)) { sending = true; setTimeout(() => { sending = false; }, 500); void send({ action: 'begin' }); return; }
    const instruction = active
      ? '本轮仍按联动指令先报告需求理解度；标记使用 MA:' + link.nonce + '，格式保持不变，未知就说未知，不编造进度。'
      : link.instruction;
    try {
      append(editor, '\n\n' + instruction);
      active = true; lastMarker = ''; sending = true; setTimeout(() => { sending = false; }, 500); void send({ action: 'begin' });
    } catch { failed = true; link = null; }
  }
  // Capture before the site's own submit handlers; never press send or create extra requests.
  document.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229) return;
    const editor = event.target.closest?.(adapter.editor);
    if (editor) injectBeforeSend(editor);
  }, true);
  document.addEventListener('click', event => {
    const button = event.target.closest?.(adapter.send);
    if (!button || button.disabled || button.getAttribute('aria-disabled') === 'true') return;
    const editor = document.querySelector(adapter.editor); if (editor) injectBeforeSend(editor);
  }, true);
  // A submit event covers native forms without guessing which unlabeled icon sends a message.
  document.addEventListener('submit', event => {
    const editor = event.target.querySelector?.(adapter.editor); if (editor) injectBeforeSend(editor);
  }, true);
  function schedule() {
    if (queued || !enabled() || !link?.ok) return;
    queued = true;
    setTimeout(async () => {
      queued = false;
      if (!enabled() || !link?.nonce) return;
      const markers = [];
      const pattern = new RegExp('\\[\\[MA:' + link.nonce + ':U=\\d{1,3};P=(?:idle|running|complete|interrupted);C=(?:\\d{1,7}|\\?);T=(?:\\d{1,7}|\\?)\\]\\]', 'g');
      // Send only a marker, never prompt/response text, links or document contents.
      const replies = document.querySelectorAll(adapter.replies);
      for (const reply of Array.from(replies).slice(-8)) {
        if (reply.closest('[data-message-author-role="user"], [data-testid="user-message"]')) continue;
        const previous = baseline.get(reply);
        const current = reply.innerText || '';
        const fresh = previous == null ? current : current.startsWith(previous) ? current.slice(previous.length) : '';
        markers.push(...(fresh.match(pattern) || []));
      }
      const marker = markers.at(-1);
      if (marker && marker !== lastMarker) { const result = await send({ action: 'marker', text: marker }); if (result?.ok) lastMarker = marker; }
    }, 500);
  }
  new MutationObserver(() => {
    if (route !== location.pathname) { route = location.pathname; void connect(true); }
    schedule();
  }).observe(document.documentElement, { subtree: true, childList: true, characterData: true });
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (message.action === 'status') respond({ enabled: options.enabled, connected: !!link?.ok, failed, name: link?.name || 'None', urlSupported: true });
    if (message.action === 'retry') { void connect(); respond({ ok: true }); }
  });
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== 'local' || !changes.options) return;
    options = changes.options.newValue || { enabled: false, inject: true };
    if (options.enabled) void connect(); else { void chrome.runtime.sendMessage({ action: 'close' }).catch(() => {}); link = null; failed = false; active = false; lastMarker = ''; }
  });
  chrome.storage.local.get('options').then(result => { options = result.options || options; void connect(); });
})();
