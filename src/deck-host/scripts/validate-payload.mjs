import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const target = process.argv[2] || 'https://bitlearn.fr';
const build = resolve('../../build'), payload = join(build, 'payload');
const hash = file => createHash('sha256').update(readFileSync(file)).digest('hex');
const version = JSON.parse(readFileSync(join(payload, 'package.json'), 'utf8')).version;
const release = await import(pathToFileURL(join(payload, 'dist/release-target.js')));
assert.equal(release.RELEASE_TARGET, target);
assert.ok(readFileSync(join(payload, 'TradeDeck.vbs'), 'utf8').includes(`Const PAQUET = "${target}"`));
assert.ok(existsSync(join(payload, 'bridge/hostfxr.dll')));
assert.ok(readFileSync(join(payload, 'dist/host.js'), 'utf8').includes(`const VERSION = '${version}'`));
const runtime = JSON.parse(execFileSync(join(payload, 'node.exe'), ['-p', 'JSON.stringify({version:process.versions.node,arch:process.arch,platform:process.platform})'], { encoding: 'utf8', windowsHide: true }));
assert.equal(runtime.version, '24.21.0'); assert.equal(runtime.arch, 'x64'); assert.equal(runtime.platform, 'win32');
assert.equal(hash(join(payload, 'node.exe')), 'ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32');
// Load the packaged native dependencies without enumerating or opening any USB device.
execFileSync(join(payload, 'node.exe'), ['-e', `
  const assert=require('node:assert/strict');
  const {Resvg}=require('@resvg/resvg-js');
  const deck=require('@elgato-stream-deck/node');
  assert.equal(typeof deck.openStreamDeck,'function');
  const png=new Resvg('<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8"><rect width="8" height="8" fill="blue"/></svg>').render().asPng();
  assert.ok(png.length>0);
`], { cwd: payload, windowsHide: true, stdio: 'pipe' });
const bridge = readFileSync(join(payload, 'bridge/StreamDeckBridge.dll'));
for (const marker of ['RejectBrowserConnection', 'SAFETY_MANDATORY_PAUSE', 'flattenAccount']) {
  assert.ok(bridge.includes(Buffer.from(marker, 'utf16le')) || bridge.includes(Buffer.from(marker)), `Bridge missing ${marker}`);
}
const addonDir = join(build, 'ninjatrader/AddOns/StreamDeck');
const sources = readdirSync(addonDir).filter(name => name.endsWith('.cs'));
assert.ok(sources.length > 10);
assert.ok(!sources.some(name => /AssemblyInfo|AssemblyAttributes|TdSwingEngine/.test(name)));
assert.ok(existsSync(join(build, 'ninjatrader/Indicators/TdSwingEngine.cs')));
assert.equal(hash(join(addonDir, 'JournalSeal.cs')), hash(resolve('../NinjaTrader.AddOn.StreamDeck/Utilities/JournalSeal.cs')));
const suffix = target === 'https://bitlearn.fr' ? '' : '-dev';
const exe = join(build, `BitlearnTradeDeck-Setup-${version}${suffix}.exe`);
const manifest = { version, target, runtime, exe, exeSha256: hash(exe), files: {} };
for (const file of ['dist/host.js','dist/bitlearn.js','dist/seal.js','dist/release-target.js','dist/server-target.js','TradeDeck.vbs','node.exe','bridge/StreamDeckBridge.dll']) manifest.files[file] = hash(join(payload, file));
manifest.files['NinjaScript/JournalSeal.cs'] = hash(join(addonDir, 'JournalSeal.cs'));
writeFileSync(join(build, target === 'https://bitlearn.fr' ? 'production-manifest.json' : 'dev-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
process.stdout.write(JSON.stringify({ version, target, exeSha256: manifest.exeSha256, bytes: readFileSync(exe).length, addonSources: sources.length, checks: 'target, launcher, runtime/checksum/native modules, standalone bridge/origin guard/safety, version, NinjaScript sources' }) + '\n');
