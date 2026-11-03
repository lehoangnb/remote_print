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
import { attachMeshTransport, discoveryCandidates } from '../src/mesh-transport.js';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
test('hairpin discovery supplies an authenticated-probe candidate without exceeding old-client limits', () => {
  assert.deepEqual(discoveryCandidates('192.168.0.1:55821', ['192.168.0.155:55821'], ['198.51.100.8']),
    ['192.168.0.1:55821', '198.51.100.8:55821', '192.168.0.155:55821']);
  assert.deepEqual(discoveryCandidates('203.0.113.7:49194', [], ['198.51.100.8']), ['203.0.113.7:49194']);
  assert.deepEqual(discoveryCandidates(null, ['192.168.0.155:55821'], ['198.51.100.8']), ['192.168.0.155:55821']);
  assert.equal(discoveryCandidates('192.168.0.1:55821', Array.from({ length: 16 }, (_, i) => `10.0.0.${i + 1}:55821`),
    ['198.51.100.8', '198.51.100.9']).length, 17);
});
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
      ws.send(JSON.stringify({ type: 'ping', port: 32000, lan: [], publicEndpoints: result.publicEndpoints ?? [] })); }, 200);
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
    const diagnostics = relay.status();
    assert.equal(diagnostics.udpAccepted, 1);
    assert.equal(diagnostics.udpReceived, 1);
    assert.match(diagnostics.clients.find(x => x.id === a.id).observedEndpoint, /^127\.0\.0\.1:\d+$/);
    assert.equal(JSON.stringify(diagnostics).includes(a.state.cookie), false);
    assert.equal(JSON.stringify(diagnostics).includes(a.state.peers[0].token), false);
    const request = Buffer.alloc(88);
    request.writeUInt16BE(1);
    request.writeUInt16BE(68, 2);
    request.writeUInt32BE(0x2112a442, 4);
    Buffer.alloc(12, 0x5a).copy(request, 8);
    request.writeUInt16BE(6, 20); // RFC 8489 USERNAME
    request.writeUInt16BE(64, 22);
    request.write(a.state.cookie, 24, 'ascii');
    const answerPromise = Promise.race([
      once(udp, 'message').then(([packet]) => packet),
      wait(2000).then(() => { throw new Error('Authenticated mesh STUN timed out'); })
    ]);
    udp.send(request, relay.udp.address().port, '127.0.0.1');
    const answer = await answerPromise;
    assert.equal(answer.length, 32);
    assert.equal(answer.readUInt16BE(0), 0x0101);
    assert.equal(answer.readUInt32BE(4), 0x2112a442);
    assert.deepEqual(answer.subarray(8, 20), request.subarray(8, 20));
    assert.equal(answer.readUInt16BE(20), 0x0020);
    assert.equal(answer.readUInt16BE(26) ^ 0x2112, udp.address().port);
    assert.deepEqual(Array.from(answer.subarray(28, 32), (value, i) => value ^ request[4 + i]), [127, 0, 0, 1]);
    assert.equal(relay.stats.stunReplies, 1);
    const forged = Buffer.from(request);
    forged[24] = forged[24] === 48 ? 49 : 48;
    udp.send(forged, relay.udp.address().port, '127.0.0.1');
    udp.send(request.subarray(0, 20), relay.udp.address().port, '127.0.0.1');
    await wait(80);
    assert.equal(relay.stats.stunReplies, 1, 'Invalid cookie and unauthenticated STUN must not reflect packets');
    a.publicEndpoints = ['198.51.100.12:45678'];
    await until(() => b.state.peers[0]?.endpoints.includes('198.51.100.12:45678'), 'STUN endpoint was not advertised');
    assert.deepEqual(relay.status().clients.find(x => x.id === a.id).publicEndpoints, a.publicEndpoints);
    const invalidCandidate = await client('two', 9);
    invalidCandidate.publicEndpoints = ['127.0.0.1:445'];
    await until(() => invalidCandidate.ws.readyState === WebSocket.CLOSED, 'Loopback public candidate was accepted');
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
