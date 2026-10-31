// Carries opaque client-to-client WireGuard datagrams. The API never receives a
// WireGuard private key and this relay has no IP forwarding or network privileges.
import crypto from 'node:crypto';
import dgram from 'node:dgram';
import net from 'node:net';
import { WebSocketServer, WebSocket } from 'ws';

const maxQueue = 1024 * 1024;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
export function wireGuardPacket(data) {
  if (data.length < 4 || data.length > 2048) return false;
  const kind = data.readUInt32LE(0);
  return kind === 1 ? data.length === 148 : kind === 2 ? data.length === 92 :
    kind === 3 ? data.length === 64 : kind === 4 && data.length >= 32 && data.length % 16 === 0;
}
function privateIp(ip) {
  if (net.isIP(ip) !== 4) return false;
  const [a, b] = ip.split('.').map(Number);
  return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
}

export function attachMeshTransport(server, store, { udpPort = 51821, udpHost = '0.0.0.0', leaseMs = 15000 } = {}) {
  const wss = new WebSocketServer({ noServer: true, maxPayload: 4096, perMessageDeflate: false });
  const udp = dgram.createSocket('udp4');
  const sessions = new Map(), cookies = new Map(), pairs = new Map(), upgrades = new Map();
  let closed = false;
  const stats = { relayedBytes: 0, droppedPackets: 0 };
  function valid(s) {
    // Store mutates this enrollment object in place on approval/rejection/key
    // rotation. Avoid an O(client-count) scan on every datagram.
    const c = s.client;
    return c?.status === 'approved' && c.room && c.tunnelPublicKey === s.key &&
      c.room === s.room && performance.now() - s.seen < leaseMs;
  }
  function allowed(a, b) { return a && b && a !== b && valid(a) && valid(b) && a.room === b.room; }
  function send(s, data, binary = false) {
    if (s.ws.readyState !== WebSocket.OPEN) return;
    if (s.ws.bufferedAmount > maxQueue) {
      if (binary) { stats.droppedPackets++; return; }
      s.ws.terminate(); return;
    }
    s.ws.send(binary ? data : JSON.stringify(data), { binary });
  }
  function view(s) {
    const peers = [];
    for (const p of sessions.values()) {
      if (!allowed(s, p)) continue;
      const pairId = [s.session, p.session].sort().join(':');
      if (!pairs.has(pairId)) pairs.set(pairId, crypto.randomBytes(32).toString('hex'));
      peers.push({ id: p.id, address: p.address, publicKey: p.key, token: pairs.get(pairId),
        endpoints: [...new Set([...(p.endpoint ? [p.endpoint] : []), ...p.lan])] });
    }
    return peers.sort((a, b) => a.id.localeCompare(b.id));
  }
  function publish(s) {
    send(s, { type: 'state', address: s.address, cookie: s.cookie, leaseMs, peers: view(s) });
  }
  function reconcile() {
    for (const s of sessions.values()) {
      if (!valid(s)) { s.ws.terminate(); continue; }
      publish(s);
    }
    const active = new Set([...sessions.values()].map(s => s.session));
    for (const key of pairs.keys()) if (key.split(':').some(id => !active.has(id))) pairs.delete(key);
  }
  server.on('upgrade', async (req, socket, head) => {
    if (req.url !== '/api/client/mesh') { socket.destroy(); return; }
    const ip = req.socket.remoteAddress;
    const recent = upgrades.get(ip) ?? { start: Date.now(), count: 0 };
    if (Date.now() - recent.start > 60000) { recent.start = Date.now(); recent.count = 0; }
    upgrades.set(ip, recent);
    if (++recent.count > 240) { socket.destroy(); return; }
    socket.setTimeout(5000, () => socket.destroy());
    try {
      const c = await store.authenticateClient(req.headers['x-device-id'], req.headers['x-device-secret']);
      if (!c || !uuid.test(c.id) || c.status !== 'approved' || !c.room || !c.tunnelPublicKey || !c.tunnelAddress) {
        socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n'); return;
      }
      if (sessions.size >= 256 && !sessions.has(c.id)) {
        socket.end('HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n'); return;
      }
      socket.setTimeout(0);
      wss.handleUpgrade(req, socket, head, ws => {
        sessions.get(c.id)?.ws.terminate();
        const s = { id: c.id, client: c, key: c.tunnelPublicKey, address: c.tunnelAddress, room: c.room, ws,
          cookie: crypto.randomBytes(32).toString('hex'), session: crypto.randomUUID(), seen: performance.now(),
          lan: [], endpoint: null, budgetAt: Date.now(), budget: 8 * 1024 * 1024, controls: 0 };
        sessions.set(s.id, s); cookies.set(s.cookie, s);
        ws.on('error', () => {});
        ws.on('close', () => { if (sessions.get(s.id) === s) sessions.delete(s.id); cookies.delete(s.cookie); });
        ws.on('message', (data, binary) => {
          if (!valid(s)) { ws.terminate(); return; }
          if (Date.now() - s.budgetAt >= 1000) { s.budgetAt = Date.now(); s.budget = 8 * 1024 * 1024; s.controls = 0; }
          s.budget -= data.length;
          if (s.budget < 0) { ws.close(1008, 'Rate limit'); return; }
          if (binary) {
            const targetId = data.subarray(0, 36).toString('ascii');
            const target = sessions.get(targetId);
            if (!uuid.test(targetId) || !allowed(s, target) || !wireGuardPacket(data.subarray(36))) {
              stats.droppedPackets++; return;
            }
            // Replace the routing header; a sender can never impersonate another node.
            const outgoing = Buffer.concat([Buffer.from(s.id, 'ascii'), data.subarray(36)]);
            send(target, outgoing, true); stats.relayedBytes += data.length - 36;
          } else {
            if (++s.controls > 10) { ws.close(1008, 'Control rate limit'); return; }
            try {
              const m = JSON.parse(data.toString());
              if (m.type !== 'ping' || !Array.isArray(m.lan) || m.lan.length > 16 ||
                  !Number.isInteger(m.port) || m.port < 1024 || m.port > 65535 || !m.lan.every(privateIp)) {
                ws.close(1008, 'Invalid control'); return;
              }
              s.lan = m.lan.map(ip => `${ip}:${m.port}`); s.seen = performance.now();
            } catch { ws.close(1008, 'Invalid JSON'); }
          }
        });
        reconcile();
      });
    } catch { socket.destroy(); }
  });
  // No UDP replies: observed endpoints are sent over the authenticated WS. An
  // untrusted UDP packet cannot turn the discovery service into an amplifier.
  udp.on('message', (data, remote) => {
    if (data.length !== 68 || data.subarray(0, 4).toString() !== 'RPM1') return;
    const s = cookies.get(data.subarray(4).toString('ascii'));
    if (!s || !valid(s) || (s.reflectedAt && Date.now() - s.reflectedAt < 1000)) return;
    s.endpoint = `${remote.address}:${remote.port}`; s.reflectedAt = Date.now();
  });
  udp.on('error', error => { console.error(`Mesh discovery UDP error: ${error.message}`); });
  udp.bind(udpPort, udpHost);
  const timer = setInterval(() => {
    reconcile();
    for (const [ip, entry] of upgrades) if (Date.now() - entry.start > 60000) upgrades.delete(ip);
  }, 2000);
  timer.unref();
  return { stats, udp, status() { return { activeClients: [...sessions.values()].filter(valid).length, ...stats }; }, close() {
    if (closed) return; closed = true; clearInterval(timer);
    for (const s of sessions.values()) s.ws.terminate();
    wss.close(); udp.close();
  } };
}
