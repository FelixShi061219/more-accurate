// Development verification only. End users do not need Node.js.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const testDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'more-accurate-mcp-'));
const exe = path.join(testDirectory, 'MoreAccurateIndicator.exe');
fs.copyFileSync(path.resolve(__dirname, '../app/MoreAccurateIndicator.exe'), exe);
const messages = [
  { jsonrpc: '2.0', id: 1, method: 'initialize', params: {} },
  { jsonrpc: '2.0', id: 2, method: 'tools/list' },
  { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'update_confidence', arguments: {
    understanding: 75, reliability: 62, activeMode: 'understanding', note: '联动自测示例分数，非当前对话评分。'
  } } },
  { jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'update_confidence', arguments: {
    understanding: 101, reliability: 62, activeMode: 'understanding'
  } } },
  { jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'update_confidence', arguments: {
    understanding: 88, note: '仅需求理解度，新版最简调用。'
  } } }
];
for (const [status, completedSteps, totalSteps] of [['running',1,4],['running',5,4],['complete',1,4],['running',undefined,undefined],['complete',4,4]]) {
  messages.push({jsonrpc:'2.0',id:messages.length+1,method:'tools/call',params:{name:'update_task_progress',arguments:{status,completedSteps,totalSteps}}});
}
const child = spawn(exe, ['--mcp'], { windowsHide: true });
let stdout = '', stderr = '';
const timeout = setTimeout(() => { child.kill(); throw new Error('MCP timed out'); }, 8000);
child.on('error', error => { clearTimeout(timeout); throw error; });
child.stdout.on('data', buffer => { stdout += buffer.toString('utf8'); });
child.stderr.on('data', buffer => { stderr += buffer.toString('utf8'); });
child.on('close', code => {
  clearTimeout(timeout);
  assert.equal(code, 0, stderr);
  const replies = stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.equal(replies.length, messages.length);
  assert.equal(replies[0].result.serverInfo.name, 'more-accurate-indicator');
  assert.equal(replies[1].result.tools[0].name, 'update_confidence');
  assert.ok(!replies[2].result.isError, JSON.stringify(replies[2]));
  assert.equal(replies[3].result.isError, true);
  assert.ok(!replies[4].result.isError, JSON.stringify(replies[4]));
  assert.equal(replies[1].result.tools[1].name, 'update_task_progress');
  assert.ok(!replies[5].result.isError);
  assert.equal(replies[6].result.isError,true,'Reject completed steps above total');
  assert.equal(replies[7].result.isError,true,'Reject prematurely completed task');
  assert.ok(!replies[8].result.isError,'Unknown totals are supported');
  assert.ok(!replies[9].result.isError);
  const taskPath = path.join(testDirectory,'data/task-progress.json');
  const task = JSON.parse(fs.readFileSync(taskPath,'utf8'));
  assert.equal(task.status,'complete');
  assert.equal(task.completedSteps,4);
  assert.equal(task.totalSteps,4);
  const statePath = path.join(testDirectory, 'data/state.json');
  const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
  assert.equal(state.understanding, 88, 'Understanding-only call must succeed');
  assert.equal(state.activeMode, 'understanding', 'Only one metric is displayed');
  assert.equal(state.reliability, undefined, 'No independent reliability score');
  assert.equal(state.note, messages[4].params.arguments.note, 'Chinese UTF-8 round trip');
  console.log('PASS: MCP schema; score; UTF-8; task progress; unknown totals; invalid/premature completion rejected.');
  fs.unlinkSync(statePath);
  fs.unlinkSync(taskPath);
  fs.rmdirSync(path.join(testDirectory, 'data'));
  fs.unlinkSync(exe);
  fs.rmdirSync(testDirectory);
});
child.stdin.end(messages.map(message => JSON.stringify(message)).join('\n') + '\n');
