// Isolated UI fixture server: no production configuration or real device data is read.
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Store } from '../server/src/store.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const data = fs.mkdtempSync(path.join(fs.mkdirSync(path.join(root, 'artifacts', 'webui'), { recursive: true }) || path.join(root, 'artifacts', 'webui'), 'fixture-'));
const store = new Store(data);
store.createRoom('Văn phòng');
store.createRoom('Kho vận');
const fixtures = [
  { name: 'DESKTOP-KETOAN', room: 'Văn phòng', status: 'approved', printers: ['HP LaserJet Pro M201n', 'Canon G1010 series', 'Brother HL-L2321D'] },
  { name: 'LAPTOP-HANHCHINH', room: 'Văn phòng', status: 'pending', printers: ['Canon LBP121/122'] },
  { name: 'PRINT-KHOVAN', room: 'Kho vận', status: 'approved', printers: ['Zebra ZD220'], offline: true },
  { name: 'CLIENT-THUNGIEM', room: null, status: 'rejected', printers: [] },
  { name: '<img src=x onerror=alert(1)>', room: null, status: 'pending', printers: ['Máy in thử <script>'] }
];
for (const fixture of fixtures) {
  const id = randomUUID();
  const client = await store.registerClient({ deviceId: id, secret: 'ui-fixture-device-secret-with-48-characters-' + id,
    name: fixture.name, printers: fixture.printers });
  if (fixture.status === 'approved') store.approveClient(id, fixture.room);
  if (fixture.status === 'rejected') store.rejectClient(id);
  if (fixture.offline) store.data.clients.find(item => item.id === id).lastSeenAt = new Date(Date.now() - 3_600_000).toISOString();
}
const clients = store.data.clients;
fs.writeFileSync(path.join(data, 'db.json'), JSON.stringify(store.data));
const server = spawn(process.execPath, ['src/index.js'], {
  cwd: path.join(root, 'server'),
  env: { ...process.env, PORT: '18445', HOST: '127.0.0.1', DATA_DIR: data,
    ADMIN_USERNAME: 'admin', ADMIN_PASSWORD: 'ui-check-password-123',
    JWT_SECRET: 'ui-fixture-jwt-secret-for-local-checks-only-2026' },
  stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true
});
server.stdout.on('data', data => process.stdout.write(data));
server.stderr.on('data', data => process.stderr.write(data));
server.on('exit', code => process.exit(code ?? 0));
for (let i = 0; i < 50; i++) {
  try {
    if ((await fetch('http://127.0.0.1:18445/health')).ok) break;
  } catch { }
  await new Promise(resolve => setTimeout(resolve, 100));
}
console.log('WebUI fixture ready: http://127.0.0.1:18445');
console.log('Fixture server PID: ' + server.pid);
// Keep online fixture records current without changing the real server.
const heartbeat = setInterval(async () => {
  for (let index = 0; index < fixtures.length; index++) {
    if (fixtures[index].offline) continue;
    try {
      await fetch('http://127.0.0.1:18445/api/client/status', { headers: {
        'x-device-id': clients[index].id, 'x-device-secret': 'ui-fixture-device-secret-with-48-characters-' + clients[index].id
      } });
    } catch { }
  }
}, 10_000);
process.on('SIGINT', () => { clearInterval(heartbeat); server.kill(); });
process.on('SIGTERM', () => { clearInterval(heartbeat); server.kill(); });
