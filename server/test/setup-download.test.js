import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import express from 'express';
import { attachSetupDownload, setupConfigMarker, setupServerUrl } from '../src/setup-download.js';

function config(bytes) {
  assert.deepEqual(bytes.subarray(-setupConfigMarker.length), setupConfigMarker);
  const end = bytes.length - setupConfigMarker.length - 4;
  const length = bytes.readUInt32LE(end);
  return JSON.parse(bytes.subarray(end - length, end).toString('utf8'));
}

test('setup download embeds its public origin, handles proxy/canonical URL and stays anonymous', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'rp-setup-download-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const setupFile = path.join(directory, 'RemotePrint.Setup.exe');
  const original = Buffer.from('MZ-setup-fixture');
  await fs.writeFile(setupFile, original);
  async function start(options = {}, trustProxy = false) {
    const app = express(); app.set('trust proxy', trustProxy);
    attachSetupDownload(app, { setupFile, ...options });
    // A static file must never take precedence over the configured download.
    app.use('/downloads', express.static(directory));
    const server = app.listen(0, '127.0.0.1');
    await new Promise(resolve => server.once('listening', resolve));
    t.after(() => new Promise(resolve => server.close(resolve)));
    return `http://127.0.0.1:${server.address().port}`;
  }
  const direct = await start();
  let response = await fetch(direct + '/downloads/RemotePrint.Setup.exe?serverUrl=https://ignored.example');
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-disposition'), /attachment.*RemotePrint.Setup.exe/);
  assert.match(response.headers.get('cache-control'), /no-store/);
  let bytes = Buffer.from(await response.arrayBuffer());
  assert.deepEqual(bytes.subarray(0, original.length), original);
  assert.deepEqual(config(bytes), { serverUrl: direct });
  response = await fetch(direct + '/downloads/RemotePrint.Setup.exe', { headers: { 'x-forwarded-host': 'spoofed.example', 'x-forwarded-proto': 'https' } });
  assert.deepEqual(config(Buffer.from(await response.arrayBuffer())), { serverUrl: direct }, 'Ignore untrusted forwarding headers');
  const proxied = await start({}, 1);
  for (const host of ['print.example.test', 'other.example.test:8443', '[::1]:7443']) {
    response = await fetch(proxied + '/downloads/RemotePrint.Setup.exe', { headers: { 'x-forwarded-host': host, 'x-forwarded-proto': 'https' } });
    assert.equal(response.status, 200);
    assert.deepEqual(config(Buffer.from(await response.arrayBuffer())), { serverUrl: `https://${host}` });
  }
  const canonical = await start({ publicServerUrl: 'https://public.example.test:9443/' }, 1);
  response = await fetch(canonical + '/downloads/RemotePrint.Setup.exe', { headers: { 'x-forwarded-host': 'internal:7443', 'x-forwarded-proto': 'http' } });
  assert.deepEqual(config(Buffer.from(await response.arrayBuffer())), { serverUrl: 'https://public.example.test:9443' });
  assert.deepEqual(await fs.readFile(setupFile), original, 'Each download leaves the shared base executable unchanged');
  for (const host of ['print.example.test', 'user:password@example.test', 'example.test/path', 'example.test?secret=1']) {
    response = await fetch(proxied + '/downloads/RemotePrint.Setup.exe', { headers: { 'x-forwarded-host': host, 'x-forwarded-proto': 'http' } });
    assert.equal(response.status, 400);
  }
  const missing = await start({ setupFile: path.join(directory, 'missing.exe') });
  assert.equal((await fetch(missing + '/downloads/RemotePrint.Setup.exe')).status, 503);
});

test('setup public URL rejects unsupported protocols, credentials and non-origin URLs', () => {
  for (const value of ['http://public.example.com', 'https://user:pass@example.com', 'https://example.com/path', 'https://example.com?key=secret', 'https://example.com/#fragment', 'file:///tmp/setup.exe', 'https://example.com\\spoofed', ' https://example.com', 'https://example.com\n', ''])
    assert.throws(() => setupServerUrl(value));
  assert.equal(setupServerUrl('https://print.example.com/'), 'https://print.example.com');
  assert.equal(setupServerUrl('http://[::1]:7443'), 'http://[::1]:7443');
  assert.throws(() => attachSetupDownload(express(), { setupFile: '', publicServerUrl: 'http://public.example.com' }));
});
