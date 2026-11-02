import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import express from 'express';
import { attachClientUpdates, validVersion } from '../src/client-updates.js';

// Small, valid deflated ZIP; no executable is ever launched by the server tests.
function packageZip(version = '1.2.0', runtime = 'win-x64', manifestOverrides = {}) {
  const files = {
    'RemotePrint.Client.exe': 'client', 'RemotePrint.Client.dll': 'dll',
    'Service/RemotePrint.Service.exe': 'service', 'Service/RemotePrint.Service.dll': 'service dll', 'translations.json': '{}'
  };
  files['build-info.json'] = JSON.stringify({ version, runtime, protocol: 'native-raw-v1', files: {}, ...manifestOverrides });
  const locals = [], central = []; let offset = 0;
  for (const [filename, value] of Object.entries(files)) {
    const name = Buffer.from(filename), bytes = Buffer.from(value), packed = deflateRawSync(bytes);
    let crc = 0xffffffff;
    for (const byte of bytes) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
    crc = (crc ^ 0xffffffff) >>> 0;
    const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50); local.writeUInt16LE(20, 4); local.writeUInt16LE(8, 8);
    local.writeUInt32LE(crc, 14); local.writeUInt32LE(packed.length, 18); local.writeUInt32LE(bytes.length, 22); local.writeUInt16LE(name.length, 26);
    const dir = Buffer.alloc(46); dir.writeUInt32LE(0x02014b50); dir.writeUInt16LE(20, 4); dir.writeUInt16LE(20, 6); dir.writeUInt16LE(8, 10);
    dir.writeUInt32LE(crc, 16); dir.writeUInt32LE(packed.length, 20); dir.writeUInt32LE(bytes.length, 24); dir.writeUInt16LE(name.length, 28); dir.writeUInt32LE(offset, 42);
    locals.push(local, name, packed); central.push(dir, name); offset += local.length + name.length + packed.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}

test('client release upload, validation, immutable download and restart persistence', async t => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'rp-updates-'));
  t.after(() => fs.rm(dataDir, { recursive: true, force: true }));
  const audit = [];
  async function start() {
    const app = express();
    app.use(express.json({ limit: '256kb' }));
    attachClientUpdates(app, { dataDir,
      adminAuth: (req, res, next) => req.headers.authorization === 'Bearer test' ? next() : res.sendStatus(401),
      audit: (...event) => audit.push(event) });
    // Ordinary application routes registered after the update middleware keep their JSON contracts.
    app.get('/api/client/status', (_req, res) => res.json({ status: 'approved' }));
    app.post('/api/client/register', (_req, res) => res.json({ status: 'pending' }));
    app.get('/api/client/peers', (_req, res) => res.json([]));
    app.get('/api/client/tunnel', (_req, res) => res.json({ mode: 'mesh-v1' }));
    const server = app.listen(0, '127.0.0.1');
    await new Promise(resolve => server.once('listening', resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    return `http://127.0.0.1:${server.address().port}`;
  }
  let base = await start();
  const headers = { authorization: 'Bearer test', 'content-type': 'application/zip' };
  const upload = (bytes, runtime = 'win-x64', auth = headers, query = '', notes = 'Test') => fetch(`${base}/api/admin/updates/${runtime}?notes=${encodeURIComponent(notes)}${query}`, { method: 'PUT', headers: auth, body: bytes });
  assert.equal((await fetch(base + '/api/client/updates/win-x64')).status, 204);
  assert.equal((await fetch(base + '/api/admin/updates')).status, 401);
  assert.equal((await upload(packageZip(), 'win-x64', { 'content-type': 'application/zip' })).status, 401);
  for (const version of ['1.2', '1.2.3-beta', '1.2.3/../a', '1.2.65535', '01.2.3', '', null, undefined, 123]) {
    assert.equal(validVersion(version), false);
    assert.equal((await upload(packageZip('1.2.0', 'win-x64', { version }))).status, 400, 'Reject missing/invalid version in the ZIP manifest');
  }
  assert.equal((await upload(Buffer.from('invalid zip'))).status, 400);
  assert.equal((await upload(packageZip(), 'win-arm64')).status, 400);
  const bytes = packageZip();
  const published = await upload(bytes);
  assert.equal(published.status, 201);
  const release = await published.json();
  const clientHeaders = { 'x-client-version': '1.1.3', 'x-client-runtime': 'win-x64' };
  const updateCommand = response => JSON.parse(Buffer.from(response.headers.get('x-client-update'), 'base64').toString('utf8'));
  for (const [route, method, body] of [['status', 'GET', { status: 'approved' }], ['register', 'POST', { status: 'pending' }], ['peers', 'GET', []], ['tunnel', 'GET', { mode: 'mesh-v1' }]]) {
    const response = await fetch(`${base}/api/client/${route}`, { method, headers: clientHeaders });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), body);
    assert.deepEqual(updateCommand(response), { version: release.version, runtime: release.runtime, size: release.size, sha256: release.sha256, downloadPath: release.downloadPath, notes: '' });
    assert.match(response.headers.get('cache-control'), /no-store/);
    assert.ok(response.headers.get('x-client-update').length < 2048);
  }
  for (const headers of [{}, { ...clientHeaders, 'x-client-version': '1.2.0' }, { ...clientHeaders, 'x-client-version': '1.2.0.0' },
    { ...clientHeaders, 'x-client-version': '1.2.1' }, { ...clientHeaders, 'x-client-version': 'bad' },
    { ...clientHeaders, 'x-client-runtime': 'win-arm64' }, { ...clientHeaders, 'x-client-runtime': 'win-x86' }]) {
    const response = await fetch(base + '/api/client/status', { headers });
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('x-client-update'), null, 'Only an older valid version with a published matching runtime receives a command');
  }
  assert.equal(release.version, '1.2.0', 'Version is derived from the ZIP without request metadata');
  assert.equal(release.sha256, createHash('sha256').update(bytes).digest('hex'));
  assert.equal(release.size, bytes.length);
  assert.equal(audit.length, 1);
  assert.deepEqual(await (await fetch(base + '/api/client/updates/win-x64')).json(), release);
  assert.match((await fetch(base + '/api/client/updates/win-x64')).headers.get('cache-control'), /no-store/);
  assert.deepEqual(Buffer.from(await (await fetch(base + release.downloadPath)).arrayBuffer()), bytes);
  assert.equal((await upload(bytes)).status, 409);
  assert.equal((await upload(packageZip('1.1.9'))).status, 409);
  assert.equal((await upload(packageZip('1.1.9'), 'win-x64', headers, '&version=99.0.0')).status, 409, 'Claiming a newer version cannot bypass the version in the ZIP');
  assert.equal((await upload(Buffer.from('truncated'))).status, 400);
  assert.equal((await (await fetch(base + '/api/client/updates/win-x64')).json()).version, '1.2.0');
  const nextRelease = await upload(packageZip('1.3.0'), 'win-x64', headers, '&version=9.9.9', 'a'.repeat(4000));
  assert.equal(nextRelease.status, 201);
  assert.equal((await nextRelease.json()).version, '1.3.0', 'Request version cannot replace the ZIP version');
  const longNotesResponse = await fetch(base + '/api/client/status', { headers: clientHeaders });
  assert.ok(longNotesResponse.headers.get('x-client-update').length < 2048, 'Maximum-length release notes never inflate command headers');
  assert.equal(updateCommand(longNotesResponse).notes, '');
  assert.deepEqual(Buffer.from(await (await fetch(base + release.downloadPath)).arrayBuffer()), bytes, 'in-flight old release remains downloadable');
  assert.equal((await upload(packageZip('1.2.0', 'win-arm64'), 'win-arm64')).status, 201);
  base = await start();
  assert.equal(updateCommand(await fetch(base + '/api/client/status', { headers: clientHeaders })).version, '1.3.0', 'Commands use the persisted newest release after server restart');
  const arm = await fetch(base + '/api/client/status', { headers: { ...clientHeaders, 'x-client-runtime': 'win-arm64' } });
  assert.equal(updateCommand(arm).runtime, 'win-arm64');
  const persisted = await (await fetch(base + '/api/admin/updates', { headers })).json();
  assert.deepEqual(persisted.map(r => r.version), ['1.3.0', '1.2.0']);
  assert.equal((await fetch(base + '/api/client/updates/win-x86')).status, 400);
  assert.equal((await fetch(base + '/api/client/updates/packages/missing.zip')).status, 404);
  const files = await fs.readdir(path.join(dataDir, 'client-updates'));
  assert.equal(files.some(file => file.endsWith('.upload') || file.endsWith('.tmp')), false);
  await fs.writeFile(path.join(dataDir, 'client-updates', 'win-x64.json'), '{damaged');
  const damagedManifest = await fetch(base + '/api/client/status', { headers: clientHeaders });
  assert.equal(damagedManifest.status, 200, 'A damaged release manifest does not interrupt ordinary API traffic');
  assert.equal(damagedManifest.headers.get('x-client-update'), null);
});
