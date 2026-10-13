import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { generateKeyPairSync, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Store } from '../src/store.js';

const port = 18443;
const base = `http://127.0.0.1:${port}`;
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-print-test-'));
const server = spawn(process.execPath, ['src/index.js'], {
  cwd: path.resolve(import.meta.dirname, '..'),
  env: {
    ...process.env,
    PORT: String(port),
    HOST: '127.0.0.1',
    DATA_DIR: dataDir,
    ADMIN_USERNAME: 'admin',
    ADMIN_PASSWORD: 'test-admin-password-123',
    JWT_SECRET: 'test-secret-with-more-than-thirty-two-characters'
  },
  stdio: ['ignore', 'pipe', 'pipe']
});

async function waitForServer() {
  for (let i = 0; i < 40; i++) {
    try { if ((await fetch(`${base}/health`)).ok) return; } catch { }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Server did not start');
}

async function json(url, options = {}) {
  const response = await fetch(`${base}${url}`, {
    ...options,
    headers: { 'content-type': 'application/json', ...options.headers }
  });
  const body = await response.json();
  return { response, body };
}

function fakeClient(name, printer) {
  const { publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  return {
    deviceId: randomUUID(),
    secret: Buffer.alloc(48, name).toString('base64'),
    name,
    publicKey: publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
    printers: [printer]
  };
}

function clientHeaders(client) {
  return { 'x-device-id': client.deviceId, 'x-device-secret': client.secret };
}

test('approval, room isolation, printer sharing and job lifecycle', async () => {
  await waitForServer();
  const a = fakeClient('Client A', 'Printer A');
  const b = fakeClient('Client B', 'Printer B');
  const c = fakeClient('Client C', 'Printer C');
  a.printers = []; // A sending-only Windows client has no local printer to share.

  for (const client of [a, b, c]) {
    const result = await json('/api/client/register', { method: 'POST', body: JSON.stringify(client) });
    assert.equal(result.response.status, 200);
    assert.equal(result.body.status, 'pending');
  }

  const login = await json('/api/admin/login', {
    method: 'POST', body: JSON.stringify({ username: 'admin', password: 'test-admin-password-123' })
  });
  assert.equal(login.response.status, 200);
  const adminHeaders = { authorization: `Bearer ${login.body.token}` };
  const defaultRooms = await json('/api/admin/rooms', { headers: adminHeaders });
  assert.deepEqual(defaultRooms.body, ['Default']);
  const createdRoom = await json('/api/admin/rooms', {
    method: 'POST', headers: adminHeaders, body: JSON.stringify({ name: 'room-one' })
  });
  assert.equal(createdRoom.response.status, 201);

  const tempName = 'Room test & QA';
  const tempCreated = await json('/api/admin/rooms', {
    method: 'POST', headers: adminHeaders, body: JSON.stringify({ name: tempName })
  });
  assert.equal(tempCreated.response.status, 201);
  const tempPath = '/api/admin/rooms/' + encodeURIComponent(tempName);
  const unauthorizedDelete = await json(tempPath, { method: 'DELETE' });
  assert.equal(unauthorizedDelete.response.status, 401);
  const deletedTemp = await json(tempPath, { method: 'DELETE', headers: adminHeaders });
  assert.equal(deletedTemp.response.status, 200);
  assert.deepEqual(deletedTemp.body, { name: tempName });
  const missingTemp = await json(tempPath, { method: 'DELETE', headers: adminHeaders });
  assert.equal(missingTemp.response.status, 404);
  assert.equal(missingTemp.body.error, 'ROOM_NOT_FOUND');
  const roomsAfterDelete = await json('/api/admin/rooms', { headers: adminHeaders });
  assert.equal(roomsAfterDelete.body.includes(tempName), false);

  for (const [client, room] of [[a, 'room-one'], [b, 'room-one'], [c, 'room-two']]) {
    const approved = await json(`/api/admin/clients/${client.deviceId}/approve`, {
      method: 'POST', headers: adminHeaders, body: JSON.stringify({ room })
    });
    assert.equal(approved.response.status, 200);
  }

  const deleteInUse = await json('/api/admin/rooms/room-one', {
    method: 'DELETE', headers: adminHeaders
  });
  assert.equal(deleteInUse.response.status, 409);
  assert.equal(deleteInUse.body.error, 'ROOM_IN_USE');
  const roomsStillAssigned = await json('/api/admin/rooms', { headers: adminHeaders });
  assert.equal(roomsStillAssigned.body.includes('room-one'), true);

  const peers = await json('/api/client/peers', { headers: clientHeaders(a) });
  assert.deepEqual(peers.body.map((x) => x.name), ['Client B']);
  assert.deepEqual(peers.body[0].printers, ['Printer B']);

  const payload = { wrappedKey: 'a2V5', nonce: 'bm9uY2U=', tag: 'dGFn', ciphertext: 'Y2lwaGVydGV4dA==' };
  const created = await json('/api/jobs', {
    method: 'POST', headers: clientHeaders(a),
    body: JSON.stringify({ targetClientId: b.deviceId, printerName: 'Printer B', fileName: 'test.pdf', contentType: 'application/pdf', payload })
  });
  assert.equal(created.response.status, 201);

  const forbidden = await json('/api/jobs', {
    method: 'POST', headers: clientHeaders(a),
    body: JSON.stringify({ targetClientId: c.deviceId, printerName: 'Printer C', fileName: 'test.pdf', contentType: 'application/pdf', payload })
  });
  assert.equal(forbidden.response.status, 403);
  assert.equal(forbidden.body.error, 'TARGET_NOT_IN_ROOM');

  const pending = await json('/api/jobs/pending', { headers: clientHeaders(b) });
  assert.equal(pending.body.length, 1);
  const downloaded = await json(`/api/jobs/${created.body.id}`, { headers: clientHeaders(b) });
  assert.deepEqual(downloaded.body.payload, payload);
  const completed = await json(`/api/jobs/${created.body.id}/status`, {
    method: 'POST', headers: clientHeaders(b), body: JSON.stringify({ status: 'completed' })
  });
  assert.equal(completed.body.status, 'completed');

  const deletedEmptyDefault = await json('/api/admin/rooms/Default', {
    method: 'DELETE', headers: adminHeaders
  });
  assert.equal(deletedEmptyDefault.response.status, 200);
  const storedRooms = JSON.parse(fs.readFileSync(path.join(dataDir, 'db.json'), 'utf8')).rooms;
  assert.equal(storedRooms.includes('Default'), false);
});

test('deleting the final Room survives a Store reload', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-print-room-test-'));
  try {
    const store = new Store(directory);
    assert.deepEqual(store.listRooms(), ['Default']);
    assert.equal(store.deleteRoom('Default'), true);
    assert.deepEqual(store.listRooms(), []);
    assert.deepEqual(new Store(directory).listRooms(), []);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test.after(() => {
  server.kill();
  fs.rmSync(dataDir, { recursive: true, force: true });
});
