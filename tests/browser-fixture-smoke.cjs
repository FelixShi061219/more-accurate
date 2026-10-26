// Controlled DOM fixtures. These do not assert compatibility with changing live websites.
const assert = require('node:assert/strict');
const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../browser-extension/content.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
async function fixture(host) {
  const messages = [], listeners = {}, timers = [], replies = [];
  let mutation, settings, runtimeListener;
  class Textarea { constructor() { this._value = 'Please inspect this.'; } get value() { return this._value; } set value(value) { this._value = value; } dispatchEvent() {} closest() { return this; } }
  const editor = new Textarea();
  const nonce = '0123456789abcdef';
  const marker = `[[MA:${nonce}:U=82;P=running;C=1;T=4]]`;
  replies.push({ innerText: marker, closest: () => null });
  const context = {
    HTMLTextAreaElement: Textarea, Event: class {},
    location: { hostname: host, pathname: '/' },
    document: { documentElement: {}, querySelectorAll: () => replies, querySelector: () => editor, addEventListener: (event, callback) => { listeners[event] = callback; } },
    MutationObserver: class { constructor(callback) { mutation = callback; } observe() {} },
    setTimeout: callback => { timers.push(callback); return timers.length; },
    chrome: {
      runtime: { sendMessage: async message => { messages.push(message); return { ok: true, nonce, name: 'Fixture Task', instruction: 'Report MA:' + nonce }; }, onMessage: { addListener: callback => { runtimeListener = callback; } } },
      storage: { local: { get: async () => ({ options: { enabled: true, inject: true } }) }, onChanged: { addListener: callback => { settings = callback; } } },
    },
  };
  vm.runInNewContext(source, context);
  await tick();
  async function flush() { for (let count = 0; timers.length && count < 20; count++) { const batch = timers.splice(0); for (const run of batch) await run(); await tick(); } }
  await flush();
  assert.equal(messages.length, 1, 'Existing response must not replay a stale marker');
  listeners.keydown({ key: 'Enter', shiftKey: true, target: editor });
  assert.equal(messages.length, 1, 'Shift+Enter must not start a request');
  listeners.keydown({ key: 'Enter', target: editor });
  assert.ok(editor.value.includes('MA:' + nonce));
  assert.equal(messages.at(-1).action, 'begin');
  await flush();
  replies.push({ innerText: 'Private response text. ' + marker, closest: () => null });
  mutation(); await flush();
  assert.deepEqual(JSON.parse(JSON.stringify(messages.at(-1))), { action: 'marker', text: marker }, 'Only the structured marker leaves the page');
  const count = messages.length; mutation(); await flush(); assert.equal(messages.length, count, 'Duplicate report is coalesced');
  settings({ options: { newValue: { enabled: true, inject: false } } }, 'local'); await tick(); await flush();
  editor.value = 'Next turn'; listeners.keydown({ key: 'Enter', target: editor }); await flush();
  assert.equal(editor.value, 'Next turn', 'Injection opt-out must be respected');
  replies.push({ innerText: marker, closest: () => null });mutation();await flush();
  assert.equal(messages.at(-1).action, 'marker', 'Identical new-turn report is accepted after begin');
  settings({ options: { newValue: { enabled: false, inject: false } } }, 'local'); await tick();
  assert.equal(messages.at(-1).action, 'close');
  let status;runtimeListener({ action: 'status' }, {}, value => { status = value; });assert.equal(status.connected, false);
}
async function backgroundFixture() {
  const storage = {}, requests = []; let listener, removed, serial = 0;
  const chrome = {
    storage: { session: {
      get: async key => ({ [key]: storage[key] }),
      set: async value => Object.assign(storage, JSON.parse(JSON.stringify(value))),
      remove: async key => { delete storage[key]; },
    } },
    runtime: {
      onMessage: { addListener: callback => { listener = callback; } },
      connectNative: () => {
        let receive, disconnect;
        return {
          onMessage: { addListener: callback => { receive = callback; } },
          onDisconnect: { addListener: callback => { disconnect = callback; } },
          postMessage: message => { requests.push(message); setImmediate(() => receive({ ok: true, id: message.id, nonce: '0123456789abcdef' })); },
          disconnect: () => disconnect(),
        };
      },
    },
    tabs: { onRemoved: { addListener: callback => { removed = callback; } } },
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../browser-extension/background.js'), 'utf8'), { chrome, URL, crypto: { randomUUID: () => String(++serial).padStart(32, '0') }, setTimeout, clearTimeout });
  const request = (action, url, extra = {}, tab = 7) => new Promise(resolve => listener({ action, ...extra }, { tab: { id: tab }, frameId: 0, url }, resolve));
  const first = await request('open', 'https://chatgpt.com/');
  const continued = await request('open', 'https://chatgpt.com/c/first', { preserve: true });
  assert.equal(continued.id, first.id, 'New chat URL transition must preserve the task');
  assert.equal(requests.filter(x => x.action === 'open').length, 1, 'URL transition must not clear a current report');
  const second = await request('open', 'https://chatgpt.com/c/second');assert.notEqual(second.id, first.id);
  const returned = await request('open', 'https://chatgpt.com/c/first');assert.equal(returned.id, first.id, 'Returning to a conversation must reuse its identity');
  await Promise.all([request('begin', 'https://chatgpt.com/c/first'), request('marker', 'https://chatgpt.com/c/first', { text: 'fixture-marker' })]);
  assert.deepEqual(requests.slice(-2).map(x => x.action), ['begin', 'marker'], 'Begin must not race past its marker');
  const otherTab = await request('open', 'https://chatgpt.com/c/first', {}, 8);assert.notEqual(otherTab.id, first.id);
  await removed(7);await removed(8);
  assert.ok(requests.some(x => x.action === 'close'));
}
(async () => {
  for (const host of ['chatgpt.com', 'chat.openai.com', 'chat.deepseek.com', 'claude.ai', 'gemini.google.com']) await fixture(host);
  await backgroundFixture();
  console.log('PASS: five controlled page adapters; opt-in; stale/duplicate suppression; new-turn reset; marker-only transfer; stable conversation identity; request ordering; disable/close. Live sites require separate verification.');
})().catch(error => { console.error(error); process.exitCode = 1; });
