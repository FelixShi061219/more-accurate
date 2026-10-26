// Isolated protocol tests; never writes to an installed indicator's state.
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
const { spawn } = require('node:child_process');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'more-accurate-bridge-'));
const exe = path.join(root, 'MoreAccurateIndicator.exe');
fs.copyFileSync(path.resolve(__dirname, '../app/MoreAccurateIndicator.exe'), exe);
function run(args, data) {
  return new Promise((resolve, reject) => {
    const child = spawn(exe, args, { windowsHide: true });
    const chunks = []; let errors = '';
    const timeout = setTimeout(() => { child.kill(); reject(Error('Bridge timeout')); }, 6000);
    child.stdout.on('data', data => chunks.push(data)); child.stderr.on('data', data => { errors += data; });
    child.on('error', reject); child.on('close', code => { clearTimeout(timeout); code === 0 ? resolve(Buffer.concat(chunks)) : reject(Error(errors || 'exit ' + code)); });
    child.stdin.end(data);
  });
}
async function native(messages) {
  const data = Buffer.concat(messages.map(message => { const body = Buffer.from(JSON.stringify(message)); const length = Buffer.alloc(4); length.writeUInt32LE(body.length); return Buffer.concat([length, body]); }));
  const output = await run(['--native-host'], data); const replies = [];
  for (let offset = 0; offset < output.length;) { const count = output.readUInt32LE(offset); offset += 4; replies.push(JSON.parse(output.subarray(offset, offset + count))); offset += count; }
  assert.equal(replies.length, messages.length); return replies;
}
(async () => {
  try {
    const providers = ['DeepSeek', 'ChatGPT', 'Claude', 'Gemini'];
    const opened = await native(providers.map((provider, i) => ({ action: 'open', id: 'web-test-' + i, provider })));
    opened.forEach(reply => { assert.equal(reply.ok, true); assert.match(reply.nonce, /^[a-f0-9]{16}$/); assert.ok(reply.instruction.includes('U=<')); });
    const marker = (nonce, u, p = 'idle', c = '?', t = '?') => `[[MA:${nonce}:U=${u};P=${p};C=${c};T=${t}]]`;
    const results = await native([
      { action: 'marker', id: 'web-test-0', provider: 'DeepSeek', text: marker(opened[0].nonce, 71, 'running', '1', '4') },
      { action: 'marker', id: 'web-test-1', provider: 'ChatGPT', text: marker(opened[1].nonce, 83) },
      { action: 'marker', id: 'web-test-2', provider: 'Claude', text: marker(opened[0].nonce, 100) },
      { action: 'marker', id: 'web-test-3', provider: 'Gemini', text: marker(opened[3].nonce, 999) },
      { action: 'marker', id: 'web-test-3', provider: 'Gemini', text: marker(opened[3].nonce, 85, 'complete', '1', '4') },
      { action: 'marker', id: 'web-test-0', provider: 'Claude', text: marker(opened[0].nonce, 99) },
      { action: 'open', id: '../escape', provider: 'Gemini' },
    ]);
    assert.equal(results[0].updated, true); assert.equal(results[1].updated, true);
    assert.equal(results[2].updated, false, 'Task nonces prevent cross-task markers');
    for (const i of [3, 4, 5, 6]) assert.equal(results[i].ok, false, JSON.stringify(results[i]));
    let sessions = JSON.parse(fs.readFileSync(path.join(root, 'data/sessions.json')));
    assert.equal(sessions[0].understanding, 71); assert.equal(sessions[0].completedSteps, 1);
    assert.equal(sessions[1].understanding, 83); assert.equal(sessions[2].understanding, null);
    const mcp = [
      { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'update_confidence', arguments: { taskId: 'codex-test-1', understanding: 65 } } },
      { jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'update_confidence', arguments: { taskId: 'codex-test-2', understanding: 91 } } },
      { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'update_task_progress', arguments: { taskId: 'codex-test-1', status: 'running', completedSteps: 2, totalSteps: 5 } } },
    ];
    const replies = (await run(['--mcp'], mcp.map(x => JSON.stringify(x)).join('\n') + '\n')).toString().trim().split(/\r?\n/).map(JSON.parse);
    replies.forEach(reply => assert.ok(!reply.result.isError));
    sessions = JSON.parse(fs.readFileSync(path.join(root, 'data/sessions.json')));
    assert.equal(sessions.find(x => x.id === 'codex-test-1').understanding, 65);
    assert.equal(sessions.find(x => x.id === 'codex-test-1').completedSteps, 2);
    assert.equal(sessions.find(x => x.id === 'codex-test-2').understanding, 91);
    assert.ok(!fs.existsSync(path.join(root, 'data/state.json')), 'Identity-aware updates do not overwrite legacy score');
    await native([{ action: 'begin', id: 'web-test-0', provider: 'DeepSeek' }, { action: 'close', id: 'web-test-1', provider: 'ChatGPT' }]);
    sessions = JSON.parse(fs.readFileSync(path.join(root, 'data/sessions.json')));
    assert.equal(sessions[0].understanding, null); assert.equal(sessions[0].status, 'idle');
    assert.equal(sessions[1].understanding, null); assert.equal(sessions[1].status, 'interrupted');
    console.log('PASS: four web providers; framed native protocol; isolated Codex tasks; nonce/provider validation; invalid progress rejection; request/close resets.');
  } finally {
    // Exact temporary files only.
    const data = path.join(root, 'data');
    if (fs.existsSync(path.join(data, 'sessions.json'))) fs.unlinkSync(path.join(data, 'sessions.json'));
    if (fs.existsSync(data)) fs.rmdirSync(data);
    fs.unlinkSync(exe); fs.rmdirSync(root);
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
