import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { Store } from '../src/store.js';
import { tunnelPolicy } from '../src/tunnel-policy.js';

test('multiple Rooms survive restart and legacy migration without transitive grants', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rp-rooms-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  let store = new Store(dir, path.join(dir, 'routing'));
  const clients = [];
  for (let i = 1; i <= 3; i++) {
    const id = randomUUID();
    await store.registerClient({ deviceId: id, secret: 's'.repeat(48), name: id, printers: [] });
    const client = store.data.clients.find(c => c.id === id);
    store.approveClient(id, i === 1 ? ['A', 'B'] : i === 2 ? 'A' : 'B');
    store.enrollTunnel(client, Buffer.alloc(32, i).toString('base64'));
    clients.push(client);
  }
  const [bridge, a, b] = clients;
  assert.throws(() => store.deleteRoom('B'), /ROOM_IN_USE/);
  assert.deepEqual(store.tunnelPeersFor(a).map(p => p.id), [bridge.id]);
  const policy = tunnelPolicy(JSON.parse(fs.readFileSync(path.join(dir, 'routing', 'state.json'))).clients);
  assert.equal(policy.groups.length, 2);
  assert.equal(policy.groups.some(g => g.includes(a.tunnelAddress) && g.includes(b.tunnelAddress)), false);
  store = new Store(dir);
  assert.deepEqual(store.data.clients[0].rooms, ['A', 'B']);
  store.rejectClient(bridge.id);
  assert.deepEqual(store.data.clients[0].rooms, []);
  assert.deepEqual(store.peersFor(store.data.clients[1]), []);
  const legacy = JSON.parse(fs.readFileSync(store.dbPath));
  delete legacy.clients[1].rooms;
  legacy.clients[0].room = 'A'; // Explicit empty rooms must override stale legacy room.
  fs.writeFileSync(store.dbPath, JSON.stringify(legacy));
  store = new Store(dir);
  assert.deepEqual(store.data.clients[1].rooms, ['A']);
  assert.deepEqual(store.data.clients[0].rooms, []);
  assert.equal(store.data.clients[0].room, null);
});
