import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import dgram from 'node:dgram';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { WebSocket } from 'ws';
import { Store } from '../src/store.js';
import { attachMeshTransport } from '../src/mesh-transport.js';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, message) {
  for (let i = 0; i < 80; i++) { if (predicate()) return; await wait(50); }
  assert.fail(message);
}
test('mesh relay authorizes each packet, keeps ciphertext opaque, discovers UDP and revokes Room/key changes', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rp-mesh-'));
  const store = new Store(dir);
  const server = http.createServer();
  const relay = attachMeshTransport(server, store, { udpHost: '127.0.0.1', udpPort: 0, leaseMs: 2500 });
  const sockets = [];
  const udp = dgram.createSocket('udp4');
  await once(relay.udp, 'listening');
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const url = `ws://127.0.0.1:${server.address().port}/api/client/mesh`;
  async function client(room, byte) {
    const input = { deviceId: randomUUID(), secret: Buffer.alloc(48, byte).toString('base64'), name: 'mesh test',
      printers: [] };
    await store.registerClient(input); store.approveClient(input.deviceId, room);
    const stored = await store.authenticateClient(input.deviceId, input.secret);
    store.enrollTunnel(stored, Buffer.alloc(32, byte).toString('base64'));
    const ws = new WebSocket(url, { headers: { 'x-device-id': input.deviceId, 'x-device-secret': input.secret } });
    sockets.push(ws);
    const result = { id: input.deviceId, ws, state: null, frames: [], heartbeat: true };
    ws.on('message', (data, binary) => { if (binary) result.frames.push(data); else result.state = JSON.parse(data); });
    ws.on('error', () => {});
    await once(ws, 'open');
    result.timer = setInterval(() => { if (result.heartbeat && ws.readyState === WebSocket.OPEN)
      ws.send(JSON.stringify({ type: 'ping', port: 32000, lan: [] })); }, 200);
    ws.on('close', () => clearInterval(result.timer));
    await until(() => result.state, 'No mesh welcome');
    return result;
  }
  try {
    const a = await client('one', 1), b = await client('one', 2), c = await client('two', 3);
    await until(() => a.state.peers.length === 1 && b.state.peers.length === 1, 'No approved peers');
    assert.equal(a.state.peers[0].publicKey, Buffer.alloc(32, 2).toString('base64'));
    assert.equal(a.state.peers[0].token, b.state.peers[0].token);
    assert.deepEqual(c.state.peers, []);
    assert.equal(JSON.stringify(a.state).includes('secretHash'), false);
    const opaque = Buffer.alloc(148, 0xab); opaque.writeUInt32LE(1);
    a.ws.send(Buffer.concat([Buffer.from(b.id), opaque]));
    await until(() => b.frames.length === 1, 'Approved opaque frame not relayed');
    assert.equal(b.frames[0].subarray(0, 36).toString(), a.id);
    assert.deepEqual(b.frames[0].subarray(36), opaque);
    a.ws.send(Buffer.concat([Buffer.from(c.id), opaque]));
    a.ws.send(Buffer.concat([Buffer.from(b.id), Buffer.from('not wireguard')]));
    await wait(100);
    assert.equal(c.frames.length, 0); assert.equal(b.frames.length, 1);
    assert.equal(relay.stats.relayedBytes, opaque.length);
    assert.equal(relay.stats.droppedPackets, 2);
    udp.send(Buffer.from('RPM1' + a.state.cookie), relay.udp.address().port, '127.0.0.1');
    await until(() => b.state.peers[0]?.endpoints.length, 'UDP endpoint not discovered');
    assert.match(b.state.peers[0].endpoints[0], /^127\.0\.0\.1:\d+$/);
    // Authorization is checked against current state, before the 2 s push tick.
    store.approveClient(b.id, 'two');
    a.ws.send(Buffer.concat([Buffer.from(b.id), opaque]));
    await wait(100); assert.equal(b.frames.length, 1);
    await until(() => b.ws.readyState === WebSocket.CLOSED && a.state.peers.length === 0, 'Room change not revoked');

    const bridge = await client(['one', 'two'], 4);
    await until(() => bridge.state.peers.length === 2 && a.state.peers.length === 1 && c.state.peers.length === 1, 'Multi-Room membership not discovered');
    assert.deepEqual(new Set(bridge.state.peers.map(p => p.id)), new Set([a.id, c.id]));
    bridge.ws.send(Buffer.concat([Buffer.from(a.id), opaque]));
    bridge.ws.send(Buffer.concat([Buffer.from(c.id), opaque]));
    await until(() => a.frames.length === 1 && c.frames.length === 1, 'Multi-Room relay did not reach both Rooms');
    a.ws.send(Buffer.concat([Buffer.from(c.id), opaque]));
    await wait(100); assert.equal(c.frames.length, 1, 'Membership must not grant transitive access');
    store.approveClient(bridge.id, ['one']);
    bridge.ws.send(Buffer.concat([Buffer.from(c.id), opaque]));
    await wait(100); assert.equal(c.frames.length, 1, 'Removed Room must revoke immediately');
    await until(() => bridge.ws.readyState === WebSocket.CLOSED && c.state.peers.length === 0, 'Changed membership must invalidate the old session');
    store.enrollTunnel(store.data.clients.find(x => x.id === a.id), Buffer.alloc(32, 7).toString('base64'));
    await until(() => a.ws.readyState === WebSocket.CLOSED, 'Old enrollment key still active');
    c.heartbeat = false;
    await until(() => c.ws.readyState === WebSocket.CLOSED, 'Expired mesh lease not revoked');
    const bad = new WebSocket(url, { headers: { 'x-device-id': randomUUID(), 'x-device-secret': 'bad' } });
    sockets.push(bad); bad.on('error', () => {});
    await new Promise(resolve => bad.once('close', resolve));
    assert.equal(bad.readyState, WebSocket.CLOSED);
  } finally {
    for (const socket of sockets) socket.terminate();
    udp.close(); relay.close(); await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
