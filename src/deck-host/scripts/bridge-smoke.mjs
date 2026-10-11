/** Runs ONLY an isolated bridge. Never imports the host, connects hardware or sends an order. */
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { spawn } from 'node:child_process';
import WebSocket from 'ws';

const scratch = mkdtempSync(join(tmpdir(), 'tradedeck-bridge-smoke-'));
const executable = resolve(process.argv[2] || 'bridge/StreamDeckBridge.exe');
const pluginUrl = 'ws://127.0.0.1:9318', addonUrl = 'ws://127.0.0.1:9319';
const profile = join(scratch, 'roaming'), local = join(scratch, 'local');
mkdirSync(profile); mkdirSync(local);
const env = { ...process.env, APPDATA: profile, LOCALAPPDATA: local,
  SDBRIDGE_PluginPort: '9318', SDBRIDGE_AddonPort: '9319',
  SDBRIDGE_AllowLiveAccounts: 'false', SDBRIDGE_DefaultAccount: '', SDBRIDGE_DefaultInstrument: '',
  SDBRIDGE_SafetyStatePath: join(scratch, 'safety.json'), SDBRIDGE_SessionStatePath: join(scratch, 'session.json'),
  SDBRIDGE_CopierStatePath: join(scratch, 'copier.json'), SDBRIDGE_JournalDirectory: join(scratch, 'journal'),
  SDBRIDGE_LogDirectory: join(scratch, 'logs'),
};
const child = spawn(executable, [], { cwd: scratch, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
let output = '', failures = '';
child.stdout.on('data', data => { output += data.toString(); });
child.stderr.on('data', data => { failures += data.toString(); });
const sockets = [];
let checks = 0;
function check(value, message) { assert.ok(value, message); checks++; process.stdout.write(`PASS ${message}\n`); }
async function ready() {
  const deadline = Date.now() + 12_000;
  while (Date.now() < deadline) {
    if (output.includes('Bridge started') && output.includes('9318') && output.includes('9319')) return;
    if (child.exitCode !== null) throw new Error(`Isolated bridge exited: ${output}\n${failures}`);
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Isolated bridge did not start: ${failures || output}`);
}
function rejectOrigin(url, origin) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url, { origin, handshakeTimeout: 3000 });
    sockets.push(socket);
    socket.once('unexpected-response', (_, response) => {
      response.resume(); socket.terminate();
      try { check(response.statusCode === 403, `browser Origin blocked on ${new URL(url).port}`); resolve(); } catch (error) { reject(error); }
    });
    socket.once('open', () => reject(new Error('Browser handshake unexpectedly accepted')));
    socket.on('error', () => { });
  });
}
function connect(url) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url, { handshakeTimeout: 3000 }); sockets.push(socket);
    socket.once('open', () => { check(true, `native client accepted on ${new URL(url).port}`); resolve(socket); });
    socket.once('error', reject);
  });
}
function state(socket, requestId) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('Read-only getState timed out')), 4000);
    const received = (data) => {
      const message = JSON.parse(data.toString());
      if (message.requestId !== requestId) return;
      clearTimeout(timeout); socket.off('message', received);
      if (message.error) return reject(new Error(message.error.code));
      resolve(message);
    };
    socket.on('message', received);
    socket.send(JSON.stringify({ type: 'command', version: '1.0', source: 'plugin', requestId, action: 'getState', payload: {} }));
  });
}
try {
  await ready();
  await rejectOrigin(pluginUrl, 'https://untrusted.invalid');
  await rejectOrigin(addonUrl, 'https://untrusted.invalid');
  await rejectOrigin(pluginUrl, 'https://bitlearn.fr');
  await rejectOrigin(addonUrl, 'null');
  const plugin = await connect(pluginUrl);
  const initial = await state(plugin, 'isolated-state-before');
  check(initial.action === 'getState', 'read-only state available without NinjaTrader');
  const addon = await connect(addonUrl);
  const addonMessages = [];
  addon.on('message', data => addonMessages.push(JSON.parse(data.toString())));
  const linked = await state(plugin, 'isolated-state-after');
  const body = linked.payload || linked.result || {};
  check(JSON.stringify(body).includes('"ntConnected":true'), 'mock native add-on connection reflected in state');
  addon.ping(); plugin.ping();
  await new Promise(resolve => setTimeout(resolve, 300));
  const forbidden = new Set(['buyMarket','sellMarket','buyLimit','sellLimit','reverse','flatten','flattenAccount','cancelOrders','cancelWorkingOrders','attachBracket','copierPanic']);
  check(addonMessages.every(message => !forbidden.has(message.action)), 'no order or market command sent');
  check(readFileSync(join(scratch, 'safety.json'), 'utf8').includes('"armed": false'), 'isolated safety remains unarmed');
} finally {
  for (const socket of sockets) { try { socket.terminate(); } catch { } }
  // This is the exact process handle created above. No process-name lookup or active app stop.
  if (child.exitCode === null) child.kill();
}
process.stdout.write(`Isolated bridge: ${checks} checks passed; PID ${child.pid} stopped.\n`);
