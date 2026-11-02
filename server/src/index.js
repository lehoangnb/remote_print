import 'dotenv/config';
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import express from 'express';
import helmet from 'helmet';
import { rateLimit } from 'express-rate-limit';
import bcrypt from 'bcryptjs';
import jwt from 'jsonwebtoken';
import { Store } from './store.js';
import { roomsFor } from './rooms.js';
import { cleanText, validateRegistration } from './validation.js';
import { isTunnelKey } from './tunnel-policy.js';
import { attachMeshTransport } from './mesh-transport.js';
import { EnrollmentInvites } from './enrollment.js';
import { attachClientUpdates } from './client-updates.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const app = express();
const port = Number(process.env.PORT || 7443);
const host = process.env.HOST || '0.0.0.0';
const jwtSecret = process.env.JWT_SECRET || '';
const adminUsername = process.env.ADMIN_USERNAME || 'admin';
const adminPassword = process.env.ADMIN_PASSWORD || '';
const tunnelEndpoint = process.env.TUNNEL_ENDPOINT || '';
const tunnelServerKey = process.env.TUNNEL_SERVER_PUBLIC_KEY || '';
const tunnelMode = process.env.TUNNEL_MODE || 'mesh';
const meshUdpPort = Number(process.env.MESH_UDP_PORT || 51821);
if (!['mesh', 'legacy', 'off'].includes(tunnelMode) || !Number.isInteger(meshUdpPort) || meshUdpPort < 1 || meshUdpPort > 65535)
  throw new Error('Invalid tunnel mode or mesh UDP port');

function validTunnelEndpoint(value) {
  if (typeof value !== 'string' || value.length > 260) return false;
  const match = /^(\[[0-9a-f:.]+\]|[a-z0-9.-]+):([0-9]{1,5})$/iu.exec(value);
  if (!match || Number(match[2]) < 1 || Number(match[2]) > 65535) return false;
  try {
    const url = new URL(`http://${value}`);
    return Boolean(url.hostname && url.pathname === '/');
  } catch { return false; }
}

if (tunnelMode === 'legacy' && (tunnelEndpoint || tunnelServerKey) && (!validTunnelEndpoint(tunnelEndpoint) || !isTunnelKey(tunnelServerKey))) {
  console.error('TUNNEL_ENDPOINT and TUNNEL_SERVER_PUBLIC_KEY must both be valid.');
  process.exit(1);
}

if (jwtSecret.length < 32 || adminPassword.length < 12) {
  console.error('JWT_SECRET must have at least 32 characters and ADMIN_PASSWORD at least 12 characters.');
  process.exit(1);
}

const adminPasswordHash = await bcrypt.hash(adminPassword, 12);

const dataDir = process.env.DATA_DIR || './data';
const tunnelStateDir = tunnelMode === 'legacy' && tunnelEndpoint ? (process.env.TUNNEL_STATE_DIR || path.join(dataDir, 'tunnel-state')) : null;
const store = new Store(dataDir, tunnelStateDir);
const invitations = new EnrollmentInvites();
if (tunnelStateDir) {
  const heartbeat = path.join(store.tunnelStateDir, 'tunnel-heartbeat');
  const beat = () => {
    try { store.writeTunnelState(); fs.writeFileSync(heartbeat, 'ok\n'); }
    catch (error) { console.error(`Tunnel heartbeat failed: ${error.message}`); }
  };
  beat();
  setInterval(beat, 3000).unref();
}
app.set('trust proxy', process.env.TRUST_PROXY ? Number(process.env.TRUST_PROXY) : false);
const csp = { defaultSrc: ["'self'"], scriptSrc: ["'self'"], styleSrc: ["'self'"] };
const secureHeaders = helmet({ contentSecurityPolicy: { directives: csp } });
const httpHeaders = helmet({
  contentSecurityPolicy: { directives: { ...csp, upgradeInsecureRequests: null } },
  crossOriginOpenerPolicy: false,
  strictTransportSecurity: false
});
// req.secure also respects TRUST_PROXY when HTTPS terminates at a trusted proxy.
app.use((req, res, next) => (req.secure ? secureHeaders : httpHeaders)(req, res, next));
app.use(express.json({ limit: '256kb' }));
app.use(rateLimit({ windowMs: 60_000, limit: 240, standardHeaders: 'draft-8', legacyHeaders: false }));
app.use(express.static(path.join(here, '..', 'public'), {
  etag: false,
  maxAge: 0,
  setHeaders: (res) => res.setHeader('Cache-Control', 'no-store')
}));

function error(res, status, code) {
  return res.status(status).json({ error: code });
}

async function clientAuth(req, res, next) {
  const client = await store.authenticateClient(req.header('x-device-id'), req.header('x-device-secret'));
  if (!client) return error(res, 401, 'CLIENT_AUTH_FAILED');
  req.client = client;
  next();
}

function approved(req, res, next) {
  if (req.client.status !== 'approved' || !roomsFor(req.client).length) return error(res, 403, 'CLIENT_NOT_APPROVED');
  next();
}

function adminAuth(req, res, next) {
  const token = req.header('authorization')?.replace(/^Bearer\s+/i, '');
  try {
    req.admin = jwt.verify(token, jwtSecret, { issuer: 'remote-print-server', audience: 'remote-print-admin' });
    next();
  } catch {
    return error(res, 401, 'ADMIN_AUTH_FAILED');
  }
}

app.get('/health', (_req, res) => res.json({ ok: true, time: new Date().toISOString() }));
attachClientUpdates(app, { dataDir, adminAuth, audit: (action, detail) => store.audit(action, detail) });

app.post('/api/client/register', async (req, res) => {
  const input = validateRegistration(req.body ?? {});
  if (!input) return error(res, 400, 'INVALID_REGISTRATION');
  if (!store.data.clients.some(client => client.id === input.deviceId)) return error(res, 403, 'ENROLLMENT_REQUIRED');
  try {
    res.json(await store.registerClient(input));
  } catch (e) {
    if (e.message === 'DEVICE_AUTH_FAILED') return error(res, 401, e.message);
    throw e;
  }
});

const enrollmentLimiter = rateLimit({ windowMs: 60_000, limit: 20, standardHeaders: 'draft-8', legacyHeaders: false });
app.post('/api/client/enroll/:id', enrollmentLimiter, async (req, res) => {
  const input = validateRegistration(req.body ?? {});
  if (!input) return error(res, 400, 'INVALID_REGISTRATION');
  if (!invitations.claim(req.params.id, req.header('x-enrollment-key'), input.deviceId, input.secret))
    return error(res, 404, 'ENROLLMENT_INVALID_OR_EXPIRED');
  try { res.setHeader('Cache-Control', 'no-store'); res.json(await store.registerClient(input)); }
  catch (e) { if (e.message === 'DEVICE_AUTH_FAILED') return error(res, 401, e.message); throw e; }
});
app.post('/api/admin/enrollment', adminAuth, (req, res) => {
  try {
    const result = invitations.create(String(req.body?.serverUrl ?? ''));
    res.setHeader('Cache-Control', 'no-store');
    store.audit('enrollment_invite_created', { username: req.admin.sub });
    res.json(result);
  } catch { return error(res, 400, 'INVALID_ENROLLMENT_SERVER_URL_OR_LIMIT'); }
});

app.get('/api/client/status', clientAuth, (req, res) => res.json(store.publicClient(req.client)));
app.get('/api/client/peers', clientAuth, approved, (req, res) => res.json(store.peersFor(req.client)));
function tunnelEnabled() { return tunnelMode === 'mesh' || (tunnelMode === 'legacy' && tunnelEndpoint); }
function tunnelResponse(client) {
  return { mode: tunnelMode === 'mesh' ? 'mesh-v1' : 'legacy', address: client.tunnelAddress,
    enrolledPublicKey: client.tunnelPublicKey, endpoint: tunnelMode === 'mesh' ? '' : tunnelEndpoint,
    serverPublicKey: tunnelMode === 'mesh' ? '' : tunnelServerKey,
    udpPort: meshUdpPort, udpHost: process.env.MESH_PUBLIC_HOST || '',
    peers: tunnelMode === 'mesh' ? [] : store.tunnelPeersFor(client) };
}
app.put('/api/client/tunnel', clientAuth, approved, (req, res) => {
  if (!tunnelEnabled()) return error(res, 503, 'TUNNEL_NOT_CONFIGURED');
  const key = req.body?.publicKey;
  if (!isTunnelKey(key) || key === tunnelServerKey) return error(res, 400, 'INVALID_TUNNEL_KEY');
  try {
    store.enrollTunnel(req.client, key);
    return res.json(tunnelResponse(req.client));
  } catch (e) {
    if (e.message === 'TUNNEL_KEY_IN_USE') return error(res, 409, e.message);
    if (e.message === 'TUNNEL_ADDRESS_EXHAUSTED') return error(res, 503, e.message);
    throw e;
  }
});
app.get('/api/client/tunnel', clientAuth, approved, (req, res) => {
  if (!tunnelEnabled()) return error(res, 503, 'TUNNEL_NOT_CONFIGURED');
  if (!req.client.tunnelPublicKey || !req.client.tunnelAddress) return error(res, 404, 'TUNNEL_NOT_ENROLLED');
  return res.json(tunnelResponse(req.client));
});
const loginLimiter = rateLimit({ windowMs: 15 * 60_000, limit: 10, standardHeaders: 'draft-8', legacyHeaders: false });
app.post('/api/admin/login', loginLimiter, async (req, res) => {
  const usernameOk = cleanText(req.body?.username, 100) === adminUsername;
  const passwordOk = await bcrypt.compare(String(req.body?.password ?? ''), adminPasswordHash);
  if (!usernameOk || !passwordOk) return error(res, 401, 'INVALID_CREDENTIALS');
  const token = jwt.sign({ sub: adminUsername, role: 'admin' }, jwtSecret, {
    expiresIn: '8h', issuer: 'remote-print-server', audience: 'remote-print-admin'
  });
  store.audit('admin_login', { username: adminUsername, ip: req.ip });
  res.json({ token });
});

app.get('/api/admin/clients', adminAuth, (_req, res) => res.json(store.listClients()));
app.get('/api/admin/rooms', adminAuth, (_req, res) => res.json(store.listRooms()));
app.post('/api/admin/rooms', adminAuth, (req, res) => {
  const room = cleanText(req.body?.name, 80);
  if (!room) return error(res, 400, 'ROOM_REQUIRED');
  return res.status(201).json({ name: store.createRoom(room) });
});
app.delete('/api/admin/rooms/:name', adminAuth, (req, res) => {
  const room = req.params.name;
  if (typeof room !== 'string' || !room.trim() || room !== room.trim() || room.length > 80)
    return error(res, 400, 'ROOM_REQUIRED');
  try {
    return store.deleteRoom(room) ? res.json({ name: room }) : error(res, 404, 'ROOM_NOT_FOUND');
  } catch (e) {
    if (e.message === 'ROOM_IN_USE') return error(res, 409, e.message);
    throw e;
  }
});
app.post('/api/admin/clients/:id/approve', adminAuth, (req, res) => {
  const rooms = req.body?.rooms ?? [req.body?.room];
  if (!Array.isArray(rooms) || rooms.length < 1 || rooms.length > 100 || rooms.some(r => typeof r !== 'string' || !r.trim() || r.trim().length > 80)) return error(res, 400, 'ROOM_REQUIRED');
  const client = store.approveClient(req.params.id, rooms.map(r => r.trim()));
  return client ? res.json(client) : error(res, 404, 'CLIENT_NOT_FOUND');
});
app.post('/api/admin/clients/:id/reject', adminAuth, (req, res) => {
  const client = store.rejectClient(req.params.id);
  return client ? res.json(client) : error(res, 404, 'CLIENT_NOT_FOUND');
});

app.use((err, req, res, _next) => {
  console.error(err);
  store.audit('server_error', { path: req.path, message: err.message });
  if (!res.headersSent) error(res, 500, 'SERVER_ERROR');
});

const certPath = process.env.TLS_CERT_PATH;
const keyPath = process.env.TLS_KEY_PATH;
const server = certPath && keyPath
  ? https.createServer({ cert: fs.readFileSync(certPath), key: fs.readFileSync(keyPath) }, app)
  : http.createServer(app);
if (tunnelMode === 'mesh') {
  const mesh = attachMeshTransport(server, store, { udpPort: meshUdpPort });
  app.get('/api/admin/mesh', adminAuth, (_req, res) => res.json(mesh.status()));
}

server.listen(port, host, () => {
  const scheme = certPath && keyPath ? 'https' : 'http';
  console.log(`Remote Print Server listening at ${scheme}://${host}:${port}`);
  if (scheme === 'http') console.warn('WARNING: HTTP mode. Use HTTPS directly or place the server behind a TLS reverse proxy before Internet use.');
});

