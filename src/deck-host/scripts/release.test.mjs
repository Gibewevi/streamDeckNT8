import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { createHmac } from 'node:crypto';
import { test } from 'node:test';
import { normalizeServerUrl, resolveServerTarget } from '../dist/server-target.js';

const scratch = mkdtempSync(join(tmpdir(), 'tradedeck-release-test-'));
process.env.APPDATA = join(scratch, 'log-profile');
const { Sealer, formeCanonique } = await import('../dist/seal.js');
const prod = 'https://bitlearn.fr', dev = 'https://dev.bitlearn.fr';

for (const value of ['http://bitlearn.fr', 'https://evil.invalid', 'file:///secret', 'https://name:password@bitlearn.fr']) {
  test(`reject untrusted endpoint ${value}`, () => assert.equal(normalizeServerUrl(value), null));
}
test('canonical origins and explicit local development', () => {
  assert.equal(normalizeServerUrl(prod + '/tradedeck/'), prod);
  assert.equal(normalizeServerUrl(dev + '/'), dev);
  assert.equal(normalizeServerUrl('http://127.0.0.1:3000/'), 'http://127.0.0.1:3000');
});
test('production package overrides every development preference', () => {
  assert.equal(resolveServerTarget({ packagedTarget: prod, argument: dev, environment: dev, configured: dev }), prod);
});
test('explicit dev package cannot fall back to production preferences', () => {
  assert.equal(resolveServerTarget({ packagedTarget: dev, argument: prod, environment: prod }), dev);
});

function clientScenario({ target = prod, configured, stored, expectedPaired, statusAfterPairing }) {
  const directory = mkdtempSync(join(scratch, 'client-'));
  cpSync(resolve('dist'), join(directory, 'dist'), { recursive: true });
  writeFileSync(join(directory, 'package.json'), '{"type":"module"}');
  writeFileSync(join(directory, 'dist/release-target.js'), `export const RELEASE_TARGET=${JSON.stringify(target)};`);
  const roaming = join(directory, 'roaming'), local = join(directory, 'local');
  mkdirSync(join(roaming, 'StreamDeckTrader'), { recursive: true });
  mkdirSync(join(local, 'StreamDeckTrader'), { recursive: true });
  if (configured) writeFileSync(join(roaming, 'StreamDeckTrader/bitlearn.json'), JSON.stringify({ url: configured }));
  if (stored) writeFileSync(join(local, 'StreamDeckTrader/device.json'), JSON.stringify(stored));
  const code = `
    const assert = await import('node:assert/strict');
    const fs = await import('node:fs');
    const {BitlearnClient}=await import('./dist/bitlearn.js');
    const client=new BitlearnClient();
    assert.default.equal(client.paired,${expectedPaired});
    const requests=[];
    globalThis.fetch=async (url,init)=>{requests.push({url,redirect:init.redirect});return new Response(JSON.stringify({token:'temporary-test-token',device:{id:9}}),{status:200});};
    await client.pair('temporary-test-code','fixture','0.29.1');
    assert.default.equal(requests[0].url,${JSON.stringify(target + '/api/tradedeck/devices/pair')});
    assert.default.equal(requests[0].redirect,'error');
    const saved=JSON.parse(fs.readFileSync(process.env.LOCALAPPDATA+'/StreamDeckTrader/device.json','utf8'));
    assert.default.equal(saved.serverOrigin,${JSON.stringify(target)});
    ${statusAfterPairing ? `
    globalThis.fetch=async ()=>new Response('{}',{status:${statusAfterPairing}});
    await client.sendJournal({executions:[],events:[]});
    assert.default.equal(client.paired,${statusAfterPairing !== 401});
    assert.default.equal(fs.existsSync(process.env.LOCALAPPDATA+'/StreamDeckTrader/device.json'),${statusAfterPairing !== 401});
    ` : ''}
  `;
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', code, '--', '--bitlearn', target === prod ? dev : prod], {
    cwd: directory, env: { ...process.env, APPDATA: roaming, LOCALAPPDATA: local, TRADEDECK_BITLEARN_URL: target === prod ? dev : prod }, encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
}
test('legacy production token is retained on production update', () => clientScenario({ stored: { token: 'legacy-test-token' }, expectedPaired: true }));
test('legacy dev token is rejected by production package', () => clientScenario({ configured: dev, stored: { token: 'legacy-test-token' }, expectedPaired: false }));
test('new origin-bound dev token is rejected even when config is missing', () => clientScenario({ stored: { token: 'origin-test-token', serverOrigin: dev }, expectedPaired: false }));
test('new production token ignores stale developer config', () => clientScenario({ configured: dev, stored: { token: 'origin-test-token', serverOrigin: prod }, expectedPaired: true }));
test('explicit dev package stays on dev and preserves its token', () => clientScenario({ target: dev, configured: prod, stored: { token: 'origin-test-token', serverOrigin: dev }, expectedPaired: true }));
test('revoked device token can re-pair on next start', () => clientScenario({ expectedPaired: false, statusAfterPairing: 401 }));
test('missing subscription retains device pairing for later renewal', () => clientScenario({ expectedPaired: false, statusAfterPairing: 403 }));

test('re-pairing rotates the sealing key, archives state and preserves unsent records', () => {
  const journal = join(scratch, 'journal');
  mkdirSync(journal);
  const oldKey = '11'.repeat(32), newKey = '22'.repeat(32);
  const spool = '{"execId":"unsent-fixture"}\n', cursor = '{"events-2026-10-11.ndjson":7}';
  writeFileSync(join(journal, 'executions-2026-10-11.ndjson'), spool);
  writeFileSync(join(journal, 'cursor.json'), cursor);
  const sealer = new Sealer(journal);
  sealer.installerCle(oldKey);
  const first = { kind: 'event', eid: 'old-key-event' };
  sealer.sceller('event', first);
  sealer.installerCle(oldKey);
  assert.equal(existsSync(join(journal, 'key-archives')), false);
  sealer.installerCle(newKey);
  const next = { kind: 'event', eid: 'new-key-event' };
  sealer.sceller('event', next);
  const expected = createHmac('sha256', Buffer.from(newKey, 'hex')).update(`${next.seq}\u001f\u001f${formeCanonique(next)}`).digest('hex');
  assert.equal(next.sig, expected);
  assert.equal(readFileSync(join(journal, 'journal.key'), 'utf8'), newKey);
  const archive = join(journal, 'key-archives', readdirSync(join(journal, 'key-archives'))[0]);
  assert.equal(readFileSync(join(archive, 'journal.key'), 'utf8'), oldKey);
  assert.equal(JSON.parse(readFileSync(join(archive, 'seal-state.json'), 'utf8')).event.sig, first.sig);
  assert.equal(readFileSync(join(journal, 'executions-2026-10-11.ndjson'), 'utf8'), spool);
  assert.equal(readFileSync(join(journal, 'cursor.json'), 'utf8'), cursor);
  const restarted = new Sealer(journal);
  const final = { kind: 'event', eid: 'after-restart' };
  restarted.sceller('event', final);
  const chained = createHmac('sha256', Buffer.from(newKey, 'hex')).update(`${final.seq}\u001f${next.sig}\u001f${formeCanonique(final)}`).digest('hex');
  assert.equal(final.sig, chained);
});

test('environment transition archives both credentials and keys without losing trading state', () => {
  const state = join(scratch, 'environment/roaming'), local = join(scratch, 'environment/local');
  mkdirSync(join(state, 'journal'), { recursive: true }); mkdirSync(local, { recursive: true });
  writeFileSync(join(state, 'bitlearn.json'), JSON.stringify({ url: dev + '/tradedeck/' }));
  for (const file of ['device.json', 'journal/journal.key', 'journal/seal-state.json', 'journal/seal-state-exec.json']) writeFileSync(join(state, file), 'fixture');
  writeFileSync(join(local, 'device.json'), 'fixture');
  const retained = ['journal/executions-2026-10-11.ndjson', 'journal/cursor.json', 'layout.json', 'safety-macro.json'];
  for (const file of retained) writeFileSync(join(state, file), `retain-${file}`);
  const result = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', resolve('packaging/configure-server.ps1'), '-BitlearnUrl', prod, '-StateDir', state, '-LocalStateDir', local], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(readFileSync(join(state, 'bitlearn.json'), 'utf8')).url, prod);
  for (const file of retained) assert.equal(readFileSync(join(state, file), 'utf8'), `retain-${file}`);
  for (const file of ['device.json', 'journal/journal.key', 'journal/seal-state.json', 'journal/seal-state-exec.json']) assert.equal(existsSync(join(state, file)), false);
  assert.equal(existsSync(join(local, 'device.json')), false);
  const archive = join(state, 'environment-archives', readdirSync(join(state, 'environment-archives'))[0]);
  assert.deepEqual(readdirSync(archive).sort(), ['device-local.json', 'device-roaming.json', 'journal.key', 'seal-state-exec.json', 'seal-state.json']);
  writeFileSync(join(local, 'device.json'), 'existing-production-token');
  const repeated = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', resolve('packaging/configure-server.ps1'), '-BitlearnUrl', prod + '/', '-StateDir', state, '-LocalStateDir', local], { encoding: 'utf8' });
  assert.equal(repeated.status, 0, repeated.stderr);
  assert.equal(readFileSync(join(local, 'device.json'), 'utf8'), 'existing-production-token');
  assert.equal(readdirSync(join(state, 'environment-archives')).length, 1);
});
