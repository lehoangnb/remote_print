// Run in a disposable Linux network namespace with wg, nft and ip in PATH:
//   unshare -Urn node server/test/relay-api-integration.mjs
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-print-api-relay-'));
const children = [];
function run(command, args, input) {
  const result = spawnSync(command, args, { input, encoding: 'utf8', timeout: 10000 });
  if (result.error || result.status !== 0) throw new Error(`${command} failed: ${result.stderr}`);
  return result.stdout.trim();
}
function keypair() {
  const privateKey = run('wg', ['genkey']);
  return { privateKey, publicKey: run('wg', ['pubkey'], privateKey + '\n') };
}
async function waitFor(condition, message, timeout = 8000) {
  const until = Date.now() + timeout;
  while (Date.now() < until) {
    try { if (await condition()) return; } catch { }
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  throw new Error(message);
}
async function availablePort() {
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return port;
}
function launch(file, args, env) {
  const child = spawn(file, args, { cwd: work, env, stdio: ['ignore', 'ignore', 'pipe'] });
  child.on('error', () => {});
  child.on('exit', (code, signal) => { child.result = `exit=${code} signal=${signal}`; });
  child.errors = '';
  child.stderr.on('data', (chunk) => { child.errors = (child.errors + chunk).slice(-4000); });
  children.push(child);
  return child;
}
async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise((resolve) => child.once('exit', resolve)), new Promise((resolve) => setTimeout(resolve, 3000))]);
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
}

try {
  run('ip', ['link', 'set', 'lo', 'up']);
  const serverKey = keypair();
  const privatePath = path.join(work, 'relay-private');
  fs.writeFileSync(privatePath, serverKey.privateKey + '\n', { mode: 0o600 });
  const port = await availablePort();
  const stateDir = path.join(work, 'state');
  const base = `http://127.0.0.1:${port}`;
  const env = { ...process.env, PORT: String(port), HOST: '127.0.0.1', DATA_DIR: path.join(work, 'data'),
    TUNNEL_MODE: 'legacy', TUNNEL_STATE_DIR: stateDir, TUNNEL_ENDPOINT: 'vpn.example.test:51820', TUNNEL_SERVER_PUBLIC_KEY: serverKey.publicKey,
    ADMIN_USERNAME: 'admin', ADMIN_PASSWORD: 'integration-password-123', JWT_SECRET: 'integration-secret-longer-than-32-bytes',
    TLS_CERT_PATH: '', TLS_KEY_PATH: '' };
  const api = launch(process.execPath, [path.join(root, 'server/src/index.js')], env);
  try { await waitFor(async () => (await fetch(base + '/health')).ok, 'API did not start'); }
  catch (error) { throw new Error(`${error.message}: ${api.result ?? 'running'} ${api.errors}`); }
  const relay = launch(process.execPath, [path.join(root, 'server/src/tunnel-controller.js')],
    { ...env, TUNNEL_PRIVATE_KEY_FILE: privatePath });
  await waitFor(() => { run('wg', ['show', 'rpwg0']); return true; }, 'Relay did not start');
  async function request(route, method = 'GET', body, headers = {}) {
    const response = await fetch(base + route, { method, headers: { 'content-type': 'application/json', ...headers },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    if (!response.ok) throw new Error(`${method} ${route}: HTTP ${response.status}`);
    return response.json();
  }
  const login = await request('/api/admin/login', 'POST', { username: 'admin', password: env.ADMIN_PASSWORD });
  const admin = { authorization: `Bearer ${login.token}` };
  const peers = [];
  for (const room of ['one', 'ONE', 'two']) {
    const deviceId = crypto.randomUUID(), secret = crypto.randomBytes(48).toString('base64'), key = keypair();
    const headers = { 'x-device-id': deviceId, 'x-device-secret': secret };
    const invite = await request('/api/admin/enrollment', 'POST', { serverUrl: base }, admin);
    const code = JSON.parse(Buffer.from(invite.code.slice(8), 'base64url').toString());
    await request('/' + code.endpoint, 'POST', { deviceId, secret, name: 'Test client', printers: [] }, { 'x-enrollment-key': code.key });
    const approval = await request(`/api/admin/clients/${deviceId}/approve`, 'POST', { room }, admin);
    if (room === 'ONE') assert.equal(approval.room, 'one');
    const info = await request('/api/client/tunnel', 'PUT', { publicKey: key.publicKey }, headers);
    peers.push({ deviceId, secret, headers, publicKey: key.publicKey, address: info.address });
  }
  await waitFor(() => {
    const keys = run('wg', ['show', 'rpwg0', 'peers']);
    const firewall = run('nft', ['list', 'table', 'inet', 'remote_print']);
    return peers.every((peer) => keys.includes(peer.publicKey)) && firewall.includes(peers[0].address) &&
      firewall.includes(peers[1].address) && !firewall.includes(peers[2].address);
  }, `Relay did not apply initial Room policy (${relay.result ?? 'running'})`);
  const snapshot = fs.readFileSync(path.join(stateDir, 'state.json'), 'utf8');
  assert(!snapshot.includes(peers[0].secret));
  assert(!snapshot.includes('secretHash'));
  await request(`/api/admin/clients/${peers[1].deviceId}/approve`, 'POST', { room: 'two' }, admin);
  await waitFor(() => {
    const firewall = run('nft', ['list', 'table', 'inet', 'remote_print']);
    return !firewall.includes(peers[0].address) && firewall.includes(peers[1].address) && firewall.includes(peers[2].address);
  }, 'Relay did not apply Room move');
  await request(`/api/admin/clients/${peers[1].deviceId}/reject`, 'POST', {}, admin);
  await waitFor(() => !run('wg', ['show', 'rpwg0', 'peers']).includes(peers[1].publicKey), 'Rejected key remained active');
  await stop(api);
  await waitFor(() => run('wg', ['show', 'rpwg0', 'peers']) === '', 'Relay did not expire stopped API', 20000);
  console.log('Live API, relay enrollment, Room changes and revocation checks passed.');
} finally {
  for (const child of children.reverse()) await stop(child);
  fs.rmSync(work, { recursive: true, force: true });
}
