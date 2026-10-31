// Real WireGuard peers + the production C# bridge, inside disposable namespaces.
// Build artifacts/mesh-linux first, then: unshare -Urn node server/test/mesh-linux.mjs
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import https from 'node:https';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import crypto from 'node:crypto';
import { Store } from '../src/store.js';
import { attachMeshTransport } from '../src/mesh-transport.js';

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-print-mesh-'));
const processes = [];
const hardNat = process.argv.includes('--hard-nat');
let relay, server;
const run = (command, args, input) => {
  const r = spawnSync(command, args, { input, encoding: 'utf8', timeout: 15000 });
  if (r.error || r.status !== 0) throw new Error(`${command} ${args.join(' ')}: ${r.error || r.stderr}`);
  return r.stdout.trim();
};
function launch(command, args, env = {}) {
  const child = spawn(command, args, { env: { ...process.env, ...env }, stdio: ['ignore', 'ignore', 'pipe'] });
  child.errors = ''; child.stderr.on('data', b => { child.errors = (child.errors + b).slice(-8000); });
  processes.push(child); return child;
}
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label) {
  for (let i = 0; i < 200; i++) {
    if (await predicate()) return;
    const failed = processes.find(p => p.exitCode !== null);
    if (failed) throw new Error(`Test child stopped: ${failed.errors}`);
    await sleep(100);
  }
  throw new Error(`Timed out: ${label}\n${processes.map(p => p.errors).join('\n')}`);
}
try {
  const runner = path.resolve('artifacts/mesh-linux/RemotePrint.Transport.Checks');
  if (!fs.existsSync(runner)) throw new Error('Publish the Linux transport test host first');
  fs.chmodSync(runner, 0o700);
  run('ip', ['link', 'set', 'lo', 'up']);
  run('ip', ['link', 'add', 'rpmesh', 'type', 'bridge']);
  run('ip', ['address', 'add', '192.168.50.1/24', 'dev', 'rpmesh']);
  run('ip', ['link', 'set', 'rpmesh', 'up']);
  run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', path.join(work, 'tls.key'),
    '-out', path.join(work, 'tls.crt'), '-days', '1', '-subj', '/CN=RemotePrint isolated mesh test',
    '-addext', 'subjectAltName=IP:192.168.50.1']);
  const store = new Store(path.join(work, 'data'));
  server = https.createServer({ cert: fs.readFileSync(path.join(work, 'tls.crt')), key: fs.readFileSync(path.join(work, 'tls.key')) });
  relay = attachMeshTransport(server, store, { udpPort: 0, udpHost: '192.168.50.1' });
  await once(relay.udp, 'listening');
  server.listen(0, '192.168.50.1'); await once(server, 'listening');
  const peers = [];
  for (let i = 0; i < 3; i++) {
    const ns = launch('unshare', ['-n', 'sleep', '300']); await sleep(100);
    const router = launch('unshare', ['-n', 'sleep', '300']); await sleep(100);
    const exec = (command, args, input) => run('nsenter', ['-t', String(ns.pid), '-n', command, ...args], input);
    const nat = (command, args, input) => run('nsenter', ['-t', String(router.pid), '-n', command, ...args], input);
    // Each client has an independent NAT router. Private LAN candidates cannot
    // reach each other, so the direct test must use observed public endpoints.
    run('ip', ['link', 'add', `host${i}`, 'type', 'veth', 'peer', 'name', `wan${i}`]);
    run('ip', ['link', 'set', `wan${i}`, 'netns', String(router.pid)]);
    run('ip', ['link', 'add', `lan${i}`, 'type', 'veth', 'peer', 'name', `client${i}`]);
    run('ip', ['link', 'set', `lan${i}`, 'netns', String(router.pid)]);
    run('ip', ['link', 'set', `client${i}`, 'netns', String(ns.pid)]);
    run('ip', ['link', 'set', `host${i}`, 'master', 'rpmesh']);
    run('ip', ['link', 'set', `host${i}`, 'up']);
    nat('ip', ['link', 'set', 'lo', 'up']);
    nat('ip', ['address', 'add', `192.168.50.${i + 2}/24`, 'dev', `wan${i}`]);
    nat('ip', ['address', 'add', `172.20.${i}.1/24`, 'dev', `lan${i}`]);
    nat('ip', ['link', 'set', `wan${i}`, 'up']); nat('ip', ['link', 'set', `lan${i}`, 'up']);
    nat('sh', ['-c', 'echo 1 > /proc/sys/net/ipv4/ip_forward']);
    const natFile = path.join(work, `nat${i}.nft`);
    fs.writeFileSync(natFile, `add table ip test_nat\nadd chain ip test_nat post { type nat hook postrouting priority 100; }\nadd rule ip test_nat post oifname "wan${i}" counter masquerade${hardNat ? ' random' : ''}\nadd chain ip test_nat forward { type filter hook forward priority 0; policy accept; }\nadd rule ip test_nat forward iifname "wan${i}" ct state new counter drop\n`);
    nat('nft', ['-f', natFile]);
    exec('ip', ['link', 'set', 'lo', 'up']);
    exec('ip', ['address', 'add', `172.20.${i}.2/24`, 'dev', `client${i}`]);
    exec('ip', ['link', 'set', `client${i}`, 'up']);
    exec('ip', ['route', 'add', 'default', 'via', `172.20.${i}.1`]);
    const key = run('wg', ['genkey']); const pub = run('wg', ['pubkey'], key + '\n');
    const keyPath = path.join(work, `key${i}`); fs.writeFileSync(keyPath, key, { mode: 0o600 });
    const identity = { deviceId: crypto.randomUUID(), secret: crypto.randomBytes(48).toString('base64'), name: `Linux${i}`,
      printers: [] };
    await store.registerClient(identity); store.approveClient(identity.deviceId, i < 2 ? 'one' : 'two');
    const client = await store.authenticateClient(identity.deviceId, identity.secret);
    const address = store.enrollTunnel(client, pub);
    const config = path.join(work, `config${i}.json`), state = path.join(work, `state${i}.json`);
    fs.writeFileSync(config, JSON.stringify({ serverUrl: `https://192.168.50.1:${server.address().port}`,
      deviceId: identity.deviceId, secret: identity.secret, address, udpHost: '192.168.50.1', udpPort: relay.udp.address().port,
      wireGuardPort: 32000 }), { mode: 0o600 });
    exec('ip', ['link', 'add', 'wgprint', 'type', 'wireguard']);
    exec('wg', ['set', 'wgprint', 'private-key', keyPath, 'listen-port', '32000']);
    exec('ip', ['address', 'add', address + '/32', 'dev', 'wgprint']);
    exec('ip', ['link', 'set', 'wgprint', 'mtu', '1280', 'up']);
    const bridge = launch('nsenter', ['-t', String(ns.pid), '-n', runner, config, state], { SSL_CERT_FILE: path.join(work, 'tls.crt') });
    await until(() => { try { return JSON.parse(fs.readFileSync(state)).authorized; } catch { return false; } }, 'client control connection');
    const localPort = JSON.parse(fs.readFileSync(state)).localUdpPort;
    // Model endpoint-independent UDP mapping explicitly. The inbound NEW drop
    // remains: peers must still open the matching outbound filter by punching.
    if (!hardNat) {
      nat('nft', ['add', 'chain', 'ip', 'test_nat', 'pre', '{ type nat hook prerouting priority -100; }']);
      nat('nft', ['add', 'rule', 'ip', 'test_nat', 'pre', 'iifname', `wan${i}`, 'udp', 'dport', String(localPort),
        'dnat', 'to', `172.20.${i}.2:${localPort}`]);
    }
    peers.push({ exec, nat, ns, bridge, config, state, pub, address, identity });
  }
  const read = p => { try { return JSON.parse(fs.readFileSync(p.state)); } catch { return {}; } };
  await until(() => peers.slice(0, 2).every(p => read(p).bindings?.length === 1) && read(peers[2]).authorized, 'mesh identities');
  assert.equal(read(peers[2]).bindings.length, 0);
  for (const p of peers.slice(0, 2)) {
    const b = read(p).bindings[0];
    p.exec('wg', ['set', 'wgprint', 'peer', b.publicKey, 'endpoint', `127.0.0.1:${b.port}`, 'allowed-ips', b.address + '/32', 'persistent-keepalive', '1']);
    p.exec('ip', ['route', 'add', b.address + '/32', 'dev', 'wgprint']);
    assert.match(p.exec('ip', ['route', 'get', '1.1.1.1']), /via 172\.20\.\d+\.1 dev client/);
  }
  launch('nsenter', ['-t', String(peers[1].ns.pid), '-n', 'python3', '-u', '-c',
    'import socket\ns=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);s.bind(("10.247.0.3",27444));s.listen()\nwhile True:\n c,_=s.accept()\n try:\n  while True:\n   b=c.recv(65536)\n   if not b:break\n   c.sendall(b)\n finally:c.close()']);
  await sleep(300);
  const handshakeReady = () => peers.slice(0, 2).every(p => p.exec('wg', ['show', 'wgprint', 'latest-handshakes'])
    .split('\n').some(line => Number(line.split('\t')[1]) > 0));
  async function transfer() {
    // Keep the control/relay server event loop live during every transfer,
    // including initial WireGuard handshakes before the direct path is used.
    const child = launch('nsenter', ['-t', String(peers[0].ns.pid), '-n', 'python3', '-c',
      'import socket,os\ns=socket.create_connection(("10.247.0.3",27444),5);s.settimeout(10);data=os.urandom(1024*1024);out=bytearray()\nfor i in range(0,len(data),16384):\n b=data[i:i+16384];s.sendall(b)\n while len(out)<i+len(b):\n  r=s.recv(65536)\n  if not r:raise RuntimeError("Unexpected EOF")\n  out.extend(r)\nassert bytes(out)==data;s.close()']);
    const [code] = await once(child, 'exit');
    processes.splice(processes.indexOf(child), 1);
    assert.equal(code, 0, child.errors);
  }
  if (!hardNat) {
  try { await until(() => peers.slice(0, 2).every(p => read(p).bindings[0]?.path === 'Trực tiếp'), 'direct UDP'); }
  catch (e) {
    for (const p of peers) { console.error(JSON.stringify(read(p))); console.error(p.nat('nft', ['list', 'ruleset'])); }
    throw e;
  }
  await until(handshakeReady, 'real WireGuard handshake before direct transfer');
  const before = relay.stats.relayedBytes;
  await transfer();
  assert.equal(relay.stats.relayedBytes, before, 'Direct document data unexpectedly used the relay');
  console.log('PASS: real WireGuard + production C# direct UDP across independent NAT routers, 1 MiB byte-for-byte, no relay document bytes; Internet default route preserved.');
  fs.writeFileSync(peers[1].config + '.force-relay', '');
  }
  await until(() => read(peers[0]).bindings[0]?.path === 'Qua relay', 'fallback to relay');
  await until(handshakeReady, 'real WireGuard handshake via relay');
  const relayBefore = relay.stats.relayedBytes;
  await transfer();
  assert.ok(relay.stats.relayedBytes > relayBefore + 1024 * 1024);
  console.log(hardNat ? 'PASS: hard NAT retains encrypted WSS relay and delivers 1 MiB intact without peer port forwarding.' :
    'PASS: same WireGuard peers survive direct-path loss and deliver 1 MiB via encrypted WSS relay.');
  store.approveClient(peers[1].identity.deviceId, 'two');
  await until(() => read(peers[0]).bindings?.length === 0, 'Room revocation');
  console.log('PASS: Room changes revoke the production packet bridge; other Room was never discoverable.');
} finally {
  for (const p of processes) if (p.exitCode === null) p.kill('SIGKILL');
  relay?.close();
  if (server) await new Promise(resolve => server.close(resolve));
  fs.rmSync(work, { recursive: true, force: true });
}
