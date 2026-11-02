import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Store } from '../src/store.js';
import { tunnelPolicy, nftPolicy, wgPolicy } from '../src/tunnel-policy.js';

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rp-api-'));
const base = 'http://127.0.0.1:18443';
const child = spawn(process.execPath, ['src/index.js'], {
  cwd: path.resolve(import.meta.dirname, '..'), windowsHide: true,
  env: {
    ...process.env, PORT: '18443', HOST: '127.0.0.1', DATA_DIR: dir, TRUST_PROXY: '1',
    ADMIN_USERNAME: 'admin', ADMIN_PASSWORD: 'test-admin-password-123',
    JWT_SECRET: 'test-secret-with-more-than-thirty-two-characters',
    TUNNEL_MODE: 'legacy', TUNNEL_ENDPOINT: 'vpn.example.test:51820',
    TUNNEL_SERVER_PUBLIC_KEY: Buffer.alloc(32, 9).toString('base64')
  },
  stdio: ['ignore', 'pipe', 'pipe']
});

async function json(url, options = {}) {
  const response = await fetch(base + url, {
    ...options, headers: { 'content-type': 'application/json', ...options.headers }
  });
  return { response, body: await response.json() };
}
const device = name => ({
  deviceId: randomUUID(), secret: Buffer.alloc(48, name).toString('base64'), name,
  printers: ['Office Printer'], windowsPrinterShares: { 'Office Printer': 'Office' }, nativePrinting: true
});
const headers = client => ({ 'x-device-id': client.deviceId, 'x-device-secret': client.secret });

test('Windows share discovery, Room approval/revocation and removed document APIs', async () => {
  for (let i = 0; i < 50; i++) {
    try { if ((await fetch(base + '/health')).ok) break; } catch { }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  const a = device('Client A'), b = device('Client B'), c = device('Client C');
  const plainPage = await fetch(base + '/');
  assert.doesNotMatch(plainPage.headers.get('content-security-policy'), /upgrade-insecure-requests/);
  assert.equal(plainPage.headers.get('cross-origin-opener-policy'), null);
  assert.equal(plainPage.headers.get('strict-transport-security'), null);
  for (const asset of ['/style.css', '/app.js']) assert.equal((await fetch(base + asset)).status, 200);
  const securePage = await fetch(base + '/', { headers: { 'x-forwarded-proto': 'https' } });
  assert.match(securePage.headers.get('content-security-policy'), /upgrade-insecure-requests/);
  assert.equal(securePage.headers.get('cross-origin-opener-policy'), 'same-origin');
  assert.ok(securePage.headers.get('strict-transport-security'));
  const login = await json('/api/admin/login', {
    method: 'POST', body: JSON.stringify({ username: 'admin', password: 'test-admin-password-123' })
  });
  assert.equal(login.response.status, 200);
  const admin = { authorization: 'Bearer ' + login.body.token };
  assert.equal((await json('/api/client/register', { method: 'POST', body: JSON.stringify(a) })).response.status, 403);
  for (const client of [a, b, c]) {
    const invite = await json('/api/admin/enrollment', { method: 'POST', headers: admin, body: JSON.stringify({serverUrl: base}) });
    assert.equal(invite.response.status, 200);
    const code = JSON.parse(Buffer.from(invite.body.code.slice(8), 'base64url').toString());
    const result = await json('/' + code.endpoint, { method: 'POST', headers: {'x-enrollment-key':code.key}, body: JSON.stringify(client) });
    assert.equal((await json('/' + code.endpoint, { method: 'POST', headers: {'x-enrollment-key':code.key}, body: JSON.stringify(device('Fake clone')) })).response.status, 404);
    assert.equal((await json('/' + code.endpoint, { method: 'POST', headers: {'x-enrollment-key':code.key}, body: JSON.stringify(client) })).response.status, 200);
    assert.equal(result.response.status, 200);
    assert.equal(result.body.status, 'pending');
    assert.equal(result.body.publicKey, undefined);
    assert.equal(result.body.printerCapabilities, undefined);
  }
  assert.equal((await json('/api/client/peers', { headers: headers(a) })).response.status, 403);
  assert.equal((await json('/api/client/register', { method: 'POST', body: JSON.stringify(a) })).response.status, 200);
  assert.equal((await json('/api/client/register', { method: 'POST', body: JSON.stringify({...a, secret:'wrong-secret-with-more-than-32-characters'}) })).response.status, 401);
  assert.equal((await json('/api/client/status', {
    headers: { ...headers(a), 'x-device-secret': 'wrong-secret' }
  })).response.status, 401);
  for (const [client, room] of [[a, 'one'], [b, 'ONE'], [c, 'two']]) {
    assert.equal((await json(`/api/admin/clients/${client.deviceId}/approve`, {
      method: 'POST', headers: admin, body: JSON.stringify({ room })
    })).response.status, 200);
  }
  const peers = (await json('/api/client/peers', { headers: headers(a) })).body;
  assert.deepEqual(peers.map(peer => peer.id), [b.deviceId]);
  assert.equal(peers[0].room, 'one');
  assert.equal(peers[0].nativePrinting, true);
  assert.deepEqual(peers[0].windowsPrinterShares, b.windowsPrinterShares);
  assert.deepEqual((await json('/api/client/peers', { headers: headers(c) })).body, []);
  const multi = await json(`/api/admin/clients/${a.deviceId}/approve`, { method: 'POST', headers: admin,
    body: JSON.stringify({ rooms: ['one', 'TWO', 'one'] }) });
  assert.equal(multi.response.status, 200);
  assert.deepEqual(multi.body.rooms, ['one', 'two']);
  assert.deepEqual(new Set((await json('/api/client/peers', { headers: headers(a) })).body.map(p => p.id)), new Set([b.deviceId, c.deviceId]));
  assert.deepEqual((await json('/api/client/peers', { headers: headers(b) })).body.map(p => p.id), [a.deviceId]);
  assert.deepEqual((await json('/api/client/peers', { headers: headers(c) })).body.map(p => p.id), [a.deviceId]);
  for (const rooms of [[], 'one', [null], [''], ['x'.repeat(81)]])
    assert.equal((await json(`/api/admin/clients/${a.deviceId}/approve`, { method: 'POST', headers: admin, body: JSON.stringify({ rooms }) })).response.status, 400);
  await json(`/api/admin/clients/${a.deviceId}/approve`, { method: 'POST', headers: admin, body: JSON.stringify({ rooms: ['one'] }) });
  assert.deepEqual((await json('/api/client/peers', { headers: headers(c) })).body, []);

  assert.equal((await json('/api/client/register', {
    method: 'POST', body: JSON.stringify({ ...b, windowsPrinterShares: { 'Office Printer': '../escape' } })
  })).response.status, 400);
  for (const [client, byte] of [[a, 1], [b, 2], [c, 3]]) {
    assert.equal((await json('/api/client/tunnel', {
      method: 'PUT', headers: headers(client),
      body: JSON.stringify({ publicKey: Buffer.alloc(32, byte).toString('base64') })
    })).response.status, 200);
  }
  assert.deepEqual((await json('/api/client/tunnel', { headers: headers(a) })).body.peers.map(peer => peer.id), [b.deviceId]);
  assert.equal((await json('/api/admin/clients')).response.status, 401);
  assert.equal((await json('/api/admin/rooms/one', { method: 'DELETE', headers: admin })).response.status, 409);
  await json(`/api/admin/clients/${b.deviceId}/reject`, { method: 'POST', headers: admin });
  assert.deepEqual((await json('/api/client/tunnel', { headers: headers(a) })).body.peers, []);
  assert.equal((await json('/api/client/tunnel', { headers: headers(b) })).response.status, 403);
  for (const [method, url] of [
    ['GET', '/api/jobs/pending'], ['POST', '/api/jobs'], ['GET', '/api/admin/jobs'],
    ['GET', '/api/admin/test-printer'], ['POST', '/api/admin/test-printer/print']
  ]) {
    assert.equal((await fetch(base + url, { method, headers: admin })).status, 404, url);
  }
  const page = await (await fetch(base)).text();
  assert.equal(page.includes('jobs-panel'), false);
  assert.equal(page.includes('test-print'), false);
});

test('migration retains device/Room/WireGuard identity, archives metadata once and removes active jobs', () => {
  const data = fs.mkdtempSync(path.join(os.tmpdir(), 'rp-migration-'));
  try {
    const id = randomUUID(), key = Buffer.alloc(32, 1).toString('base64');
    const original = JSON.stringify({
      rooms: ['Office'], jobs: [{ id: randomUUID() }], clients: [{
        id, name: 'Windows PC', secretHash: 'unchanged', status: 'approved', room: 'Office', printers: ['Office'],
        publicKey: 'obsolete-document-key', printerCapabilities: {}, printerFeatures: {},
        tunnelAddress: '10.247.0.2', tunnelPublicKey: key
      }]
    });
    fs.writeFileSync(path.join(data, 'db.json'), original);
    const store = new Store(data);
    assert.equal(store.listClients()[0].id, id);
    assert.equal(store.listClients()[0].room, 'Office');
    assert.equal(store.listClients()[0].tunnel.address, '10.247.0.2');
    assert.equal(store.data.clients[0].secretHash, 'unchanged');
    assert.equal(store.data.clients[0].tunnelPublicKey, key);
    assert.equal(store.data.jobs, undefined);
    assert.equal(store.data.clients[0].publicKey, undefined);
    assert.equal(store.data.clients[0].printerCapabilities, undefined);
    assert.equal(store.data.clients[0].printerFeatures, undefined);
    assert.equal(fs.existsSync(path.join(data, 'jobs')), false);
    assert.equal(fs.readFileSync(path.join(data, 'db-before-network-only.json'), 'utf8'), original);
    assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'db.json'))).schemaVersion, 2);
    store.rejectClient(id);
    assert.equal(new Store(data).listClients()[0].status, 'rejected');
    assert.equal(fs.readFileSync(path.join(data, 'db-before-network-only.json'), 'utf8'), original);
  } finally { fs.rmSync(data, { recursive: true, force: true }); }
});

test('tunnel keys/addresses, heartbeat persistence and empty Rooms survive restart', async () => {
  const data = fs.mkdtempSync(path.join(os.tmpdir(), 'rp-store-'));
  try {
    const store = new Store(data), a = device('A1'), b = device('B1');
    await store.registerClient(a); await store.registerClient(b);
    store.approveClient(a.deviceId, 'one'); store.approveClient(b.deviceId, 'one');
    const ca = await store.authenticateClient(a.deviceId, a.secret);
    const cb = await store.authenticateClient(b.deviceId, b.secret);
    const key = Buffer.alloc(32, 1).toString('base64'), address = store.enrollTunnel(ca, key);
    store.rejectClient(a.deviceId);
    assert.throws(() => store.enrollTunnel(cb, key), /TUNNEL_KEY_IN_USE/);
    const next = new Store(data);
    next.approveClient(a.deviceId, 'one');
    assert.equal(next.enrollTunnel(next.data.clients.find(client => client.id === a.deviceId), key), address);
    next.lastSeenPersisted.set(a.deviceId, Date.now() - 16000);
    await next.authenticateClient(a.deviceId, a.secret);
    assert.ok(next.lastSeenPersisted.get(a.deviceId) > Date.now() - 2000);
    assert.equal(next.deleteRoom('Default'), true);
    assert.equal(new Store(data).listRooms().includes('Default'), false);
  } finally { fs.rmSync(data, { recursive: true, force: true }); }
});

test('relay policy rejects duplicate identities and bounds Room heartbeat', () => {
  const peer = (room, address, byte) => ({
    status: 'approved', room, lastSeenAt: new Date().toISOString(),
    tunnelAddress: address, tunnelPublicKey: Buffer.alloc(32, byte).toString('base64')
  });
  const a = peer('one', '10.247.0.2', 1), b = peer('one', '10.247.0.3', 2), c = peer('two', '10.247.0.4', 3);
  const policy = tunnelPolicy([a, b, c]);
  assert.deepEqual(policy.groups, [['10.247.0.2', '10.247.0.3']]);
  assert.throws(() => tunnelPolicy([a, { ...b, tunnelAddress: a.tunnelAddress }]));
  assert.throws(() => tunnelPolicy([a, { ...b, tunnelPublicKey: a.tunnelPublicKey }]));
  assert.equal(tunnelPolicy([{ ...a, lastSeenAt: new Date(Date.now() - 31000).toISOString() }]).peers.length, 0);
  assert.match(nftPolicy(policy), /iifname "rpwg0" drop/);
  assert.match(wgPolicy(policy, Buffer.alloc(32, 9).toString('base64'), 51820), /AllowedIPs = 10\.247\.0\.2\/32/);
});

test.after(async () => {
  child.kill();
  if (child.exitCode === null) await new Promise(resolve => child.once('exit', resolve));
  fs.rmSync(dir, { recursive: true, force: true });
});
