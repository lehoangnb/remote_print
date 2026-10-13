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
import { cleanText, validateRegistration } from './validation.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const app = express();
const port = Number(process.env.PORT || 7443);
const host = process.env.HOST || '0.0.0.0';
const jwtSecret = process.env.JWT_SECRET || '';
const adminUsername = process.env.ADMIN_USERNAME || 'admin';
const adminPassword = process.env.ADMIN_PASSWORD || '';
const maxDocumentMb = Math.max(1, Math.min(100, Number(process.env.MAX_DOCUMENT_MB || 25)));
const retentionDays = Math.max(1, Number(process.env.JOB_RETENTION_DAYS || 7));

if (jwtSecret.length < 32 || adminPassword.length < 12) {
  console.error('JWT_SECRET must have at least 32 characters and ADMIN_PASSWORD at least 12 characters.');
  process.exit(1);
}

const adminPasswordHash = await bcrypt.hash(adminPassword, 12);

const store = new Store(process.env.DATA_DIR || './data');
app.set('trust proxy', process.env.TRUST_PROXY ? Number(process.env.TRUST_PROXY) : false);
app.use(helmet({ contentSecurityPolicy: { directives: { defaultSrc: ["'self'"], scriptSrc: ["'self'"], styleSrc: ["'self'"] } } }));
app.use(express.json({ limit: `${maxDocumentMb + 8}mb` }));
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
  if (req.client.status !== 'approved' || !req.client.room) return error(res, 403, 'CLIENT_NOT_APPROVED');
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

app.post('/api/client/register', async (req, res) => {
  const input = validateRegistration(req.body ?? {});
  if (!input) return error(res, 400, 'INVALID_REGISTRATION');
  try {
    res.json(await store.registerClient(input));
  } catch (e) {
    if (e.message === 'DEVICE_AUTH_FAILED') return error(res, 401, e.message);
    throw e;
  }
});

app.get('/api/client/status', clientAuth, (req, res) => res.json(store.publicClient(req.client)));
app.get('/api/client/peers', clientAuth, approved, (req, res) => res.json(store.peersFor(req.client)));
app.get('/api/jobs/pending', clientAuth, approved, (req, res) => res.json(store.pendingJobs(req.client)));
app.get('/api/jobs/:id', clientAuth, approved, (req, res) => {
  const result = store.getJobForTarget(req.client, req.params.id);
  return result ? res.json(result) : error(res, 404, 'JOB_NOT_FOUND');
});

app.post('/api/jobs', clientAuth, approved, (req, res) => {
  const body = req.body ?? {};
  const input = {
    targetClientId: cleanText(body.targetClientId, 36),
    printerName: cleanText(body.printerName, 200),
    fileName: cleanText(body.fileName, 200),
    contentType: cleanText(body.contentType, 100)
  };
  const payload = body.payload ?? {};
  const encryptedBytes = typeof payload.ciphertext === 'string' ? Buffer.byteLength(payload.ciphertext, 'base64') : 0;
  if (!input.targetClientId || !input.printerName || !input.fileName || input.contentType !== 'application/pdf' ||
      !payload.wrappedKey || !payload.nonce || !payload.tag || !payload.ciphertext || encryptedBytes < 1 || encryptedBytes > maxDocumentMb * 1024 * 1024) {
    return error(res, 400, 'INVALID_JOB');
  }
  try {
    return res.status(201).json(store.createJob(req.client, input, payload));
  } catch (e) {
    if (['TARGET_NOT_IN_ROOM', 'PRINTER_NOT_SHARED'].includes(e.message)) return error(res, 403, e.message);
    throw e;
  }
});

app.post('/api/jobs/:id/status', clientAuth, approved, (req, res) => {
  const status = cleanText(req.body?.status, 20);
  const message = cleanText(req.body?.error, 1000) || null;
  if (!['completed', 'failed'].includes(status)) return error(res, 400, 'INVALID_STATUS');
  const job = store.updateJob(req.client, req.params.id, status, message);
  return job ? res.json(job) : error(res, 404, 'JOB_NOT_FOUND');
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
app.get('/api/admin/jobs', adminAuth, (_req, res) => res.json(store.listJobs()));
app.get('/api/admin/rooms', adminAuth, (_req, res) => res.json(store.listRooms()));
app.post('/api/admin/rooms', adminAuth, (req, res) => {
  const room = cleanText(req.body?.name, 80);
  if (!room) return error(res, 400, 'ROOM_REQUIRED');
  return res.status(201).json({ name: store.createRoom(room) });
});
app.post('/api/admin/clients/:id/approve', adminAuth, (req, res) => {
  const room = cleanText(req.body?.room, 80);
  if (room.length < 1) return error(res, 400, 'ROOM_REQUIRED');
  const client = store.approveClient(req.params.id, room);
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

setInterval(() => store.cleanup(retentionDays), 6 * 60 * 60_000).unref();
store.cleanup(retentionDays);

const certPath = process.env.TLS_CERT_PATH;
const keyPath = process.env.TLS_KEY_PATH;
const server = certPath && keyPath
  ? https.createServer({ cert: fs.readFileSync(certPath), key: fs.readFileSync(keyPath) }, app)
  : http.createServer(app);

server.listen(port, host, () => {
  const scheme = certPath && keyPath ? 'https' : 'http';
  console.log(`Remote Print Server listening at ${scheme}://${host}:${port}`);
  if (scheme === 'http') console.warn('WARNING: HTTP mode. Use HTTPS directly or place the server behind a TLS reverse proxy before Internet use.');
});

