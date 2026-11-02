// Windows-only acceptance of the actual small EXE, against isolated HTTP servers.
// Run after publish-client.ps1: node --test test/setup-bootstrapper.test.js
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import { spawn } from 'node:child_process';
import express from 'express';
import { attachClientUpdates } from '../src/client-updates.js';
import { attachSetupDownload, setupConfigMarker } from '../src/setup-download.js';

const root = fileURLToPath(new URL('../../', import.meta.url));
const executable = path.join(root, 'dist', 'RemotePrint.Setup.exe');
const runtime = process.arch === 'arm64' ? 'win-arm64' : 'win-x64';
const packageRoot = path.join(root, 'dist', `client-${runtime}`);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

function crc32(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) {
    value ^= byte;
    for (let i = 0; i < 8; i++) value = (value >>> 1) ^ (0xedb88320 & -(value & 1));
  }
  return (value ^ 0xffffffff) >>> 0;
}
function zip(entries) {
  const local = [], central = []; let offset = 0;
  for (const [name, bytes] of entries) {
    const filename = Buffer.from(name), packed = deflateRawSync(bytes), crc = crc32(bytes);
    const header = Buffer.alloc(30); header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4);
    header.writeUInt16LE(8, 8); header.writeUInt32LE(crc, 14); header.writeUInt32LE(packed.length, 18);
    header.writeUInt32LE(bytes.length, 22); header.writeUInt16LE(filename.length, 26);
    local.push(header, filename, packed);
    const entry = Buffer.alloc(46); entry.writeUInt32LE(0x02014b50); entry.writeUInt16LE(20, 4);
    entry.writeUInt16LE(20, 6); entry.writeUInt16LE(8, 10); entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(packed.length, 20); entry.writeUInt32LE(bytes.length, 24);
    entry.writeUInt16LE(filename.length, 28); entry.writeUInt32LE(offset, 42);
    central.push(entry, filename); offset += header.length + filename.length + packed.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, directory, end]);
}

test('small setup downloads real release, rejects unsafe/corrupt packages without installing', { skip: process.platform !== 'win32', timeout: 120000 }, async t => {
  await fs.access(executable);
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'rp-setup-'));
  t.after(() => fs.rm(temp, { recursive: true, force: true }));
  const manifest = JSON.parse((await fs.readFile(path.join(packageRoot, 'build-info.json'), 'utf8')).replace(/^\uFEFF/, ''));
  const entries = [];
  // Keep the fixture small while retaining real EXE/DLL bytes and a full hash manifest.
  for (const name of ['RemotePrint.Client.exe', 'RemotePrint.Client.dll', 'Service/RemotePrint.Service.exe', 'Service/RemotePrint.Service.dll', 'translations.json'])
    entries.push([name, await fs.readFile(path.join(packageRoot, name))]);
  const fixtureManifest = { ...manifest, files: Object.fromEntries(entries.map(([name, bytes]) => [name, hash(bytes)])) };
  entries.push(['build-info.json', Buffer.from(JSON.stringify(fixtureManifest))]);
  const bytes = zip(entries);
  let release = { version: manifest.version, runtime, notes: '', size: bytes.length, sha256: hash(bytes), downloadPath: `/api/client/updates/packages/${hash(bytes)}.zip` };
  let mode = 'normal', payload = bytes;
  const app = express();
  attachSetupDownload(app, { setupFile: executable });
  app.use(express.static(path.join(root, 'server', 'public')));
  attachClientUpdates(app, { dataDir: path.join(temp, 'data'), adminAuth: (_req, _res, next) => next(), audit: () => {} });
  const official = app.listen(0, '127.0.0.1');
  await new Promise(resolve => official.once('listening', resolve));
  t.after(() => new Promise(resolve => official.close(resolve)));
  const setupDownload = await fetch(`http://127.0.0.1:${official.address().port}/downloads/RemotePrint.Setup.exe`);
  assert.equal(setupDownload.status, 200);
  const configuredExe = Buffer.from(await setupDownload.arrayBuffer());
  const baseExe = await fs.readFile(executable);
  assert.deepEqual(configuredExe.subarray(0, baseExe.length), baseExe, 'Server preserves the built executable and appends only URL configuration');
  const fullPackage = await fs.readFile(path.join(root, 'dist', `RemotePrint.Client-${runtime}.zip`));
  const published = await fetch(`http://127.0.0.1:${official.address().port}/api/admin/updates/${runtime}`, { method: 'PUT', headers: { 'content-type': 'application/zip' }, body: fullPackage });
  assert.equal(published.status, 201);
  const fixture = express();
  fixture.get('/api/client/updates/:runtime', (_req, res) => {
    if (mode === 'empty') return res.sendStatus(204);
    if (mode === 'redirect') return res.redirect('https://example.com/untrusted');
    if (mode === 'oversize-json') return res.send(' '.repeat(16385));
    res.json(release);
  });
  fixture.get('/api/client/updates/packages/:name', (_req, res) => res.type('application/zip').send(payload));
  const server = fixture.listen(0, '127.0.0.1');
  await new Promise(resolve => server.once('listening', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  let number = 0;
  async function run(url, expected, label, binary = executable) {
    const output = path.join(temp, `download-${++number}`);
    const code = await new Promise((resolve, reject) => {
      const child = spawn(binary, ['--download-only', ...(url ? ['--server', url] : []), '--output', output, '--language', 'en'], { windowsHide: true, stdio: 'ignore' });
      child.once('error', reject); child.once('exit', resolve);
    });
    assert.equal(code, expected, label + (code ? ': ' + await fs.readFile(path.join(output, 'error.txt'), 'utf8') : ''));
    return output;
  }
  const output = await run(`http://127.0.0.1:${official.address().port}`, 0, 'Use the real upload/manifest/package API, including the same assembly version');
  assert.deepEqual(await fs.readFile(path.join(output, 'package', 'RemotePrint.Client.dll')), entries[1][1]);
  const downloadedPath = path.join(temp, 'Copied and renamed setup.exe');
  await fs.writeFile(downloadedPath, configuredExe);
  const prefilled = await run(null, 0, 'Downloaded/copied/renamed EXE automatically uses its embedded URL without --server', downloadedPath);
  assert.deepEqual(await fs.readFile(path.join(prefilled, 'package', 'RemotePrint.Client.dll')), entries[1][1]);
  await run(`http://127.0.0.1:${official.address().port}`, 0, 'Explicit --server overrides embedded defaults', downloadedPath);
  // Invalid/truncated overlays fall back to manual entry; they never supply an unsafe URL.
  for (const config of ['{"serverUrl":"http://public.example.com"}', '{invalid-json', 'null', '{"serverUrl":"https://example.com/?secret=1"}']) {
    const bytes = Buffer.from(config), length = Buffer.alloc(4); length.writeUInt32LE(bytes.length);
    const invalidExe = path.join(temp, `invalid-${++number}.exe`);
    await fs.writeFile(invalidExe, Buffer.concat([baseExe, bytes, length, setupConfigMarker]));
    const code = await new Promise((resolve, reject) => {
      const child = spawn(invalidExe, ['--download-only', '--output', path.join(temp, `invalid-${++number}`)], { windowsHide: true, stdio: 'ignore' });
      child.once('error', reject); child.once('exit', resolve);
    });
    assert.equal(code, 2, 'Malformed overlay does not become a default server: ' + config);
  }
  const pristine = release;
  for (const value of ['empty', 'redirect', 'oversize-json']) {
    mode = value; await run(base, 1, value);
  }
  mode = 'normal';
  for (const change of [{ sha256: '0'.repeat(64), downloadPath: `/api/client/updates/packages/${'0'.repeat(64)}.zip` }, { runtime: 'win-x86' }, { downloadPath: 'https://example.com/payload.exe' }, { size: bytes.length + 1 }]) {
    release = { ...pristine, ...change }; await run(base, 1, JSON.stringify(change));
  }
  release = pristine;
  await run('http://print.example.com', 1, 'Reject non-loopback HTTP');
  await run('https://user:pass@example.com', 1, 'Reject credentials in URL');
  for (const mutation of ['traversal', 'extra-file', 'changed-file', 'missing-file', 'wrong-manifest-runtime', 'wrong-assembly-version', 'duplicate-name']) {
    let modified = entries.map(([name, data]) => [name, data]);
    if (mutation === 'traversal') modified.push(['../escape.exe', Buffer.from('invalid')]);
    if (mutation === 'extra-file') modified.push(['extra.exe', Buffer.from('invalid')]);
    if (mutation === 'changed-file') modified[1] = [modified[1][0], Buffer.from('corrupt')];
    if (mutation === 'missing-file') modified = modified.filter(([name]) => name !== 'Service/RemotePrint.Service.dll');
    if (mutation === 'duplicate-name') modified.push(['REMOTEPRINT.CLIENT.EXE', entries[0][1]]);
    if (mutation === 'wrong-manifest-runtime') modified[modified.length - 1] = ['build-info.json', Buffer.from(JSON.stringify({ ...fixtureManifest, runtime: 'win-x86' }))];
    if (mutation === 'wrong-assembly-version') modified[modified.length - 1] = ['build-info.json', Buffer.from(JSON.stringify({ ...fixtureManifest, version: '99.0.0' }))];
    payload = zip(modified);
    release = { ...pristine, ...(mutation === 'wrong-assembly-version' ? { version: '99.0.0' } : {}), size: payload.length, sha256: hash(payload), downloadPath: `/api/client/updates/packages/${hash(payload)}.zip` };
    await run(base, 1, mutation);
  }
  await assert.rejects(fs.access(path.join(temp, 'escape.exe')));
  assert.ok((await fs.stat(executable)).size < 512 * 1024, 'Setup stays under 512 KiB');
});
