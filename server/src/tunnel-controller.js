// Run only inside the relay's dedicated network namespace with CAP_NET_ADMIN.
// Never expose this process or the WireGuard private key to the web container.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { tunnelPolicy, nftPolicy, wgPolicy, isTunnelKey } from './tunnel-policy.js';

const dataDir = process.env.TUNNEL_STATE_DIR || '/app/tunnel-state';
const keyPath = process.env.TUNNEL_PRIVATE_KEY_FILE || '/run/secrets/tunnel_private_key';
const port = Number(process.env.TUNNEL_LISTEN_PORT || 51820);
const expectedPublicKey = process.env.TUNNEL_SERVER_PUBLIC_KEY || '';
const heartbeatPath = `${dataDir}/tunnel-heartbeat`;
const databasePath = `${dataDir}/state.json`;

function run(command, args, input) {
  const result = spawnSync(command, args, { input, encoding: 'utf8', timeout: 10000 });
  if (result.error || result.status !== 0)
    throw new Error(`${command} failed: ${result.error?.message || (result.stderr || '').trim()}`);
  return result.stdout.trim();
}

const privateKey = fs.readFileSync(keyPath, 'utf8').trim();
if (!isTunnelKey(privateKey) || !isTunnelKey(expectedPublicKey) ||
    run('wg', ['pubkey'], `${privateKey}\n`) !== expectedPublicKey)
  throw new Error('Relay private key does not match the configured public key');

let started = false;
let fingerprint = '';
let lastFirewallRefresh = 0;
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-print-relay-'));
function applyFile(name, content, command, args) {
  const file = path.join(scratch, name);
  fs.writeFileSync(file, content, { mode: 0o600 });
  try { run(command, [...args, file]); }
  finally { fs.rmSync(file, { force: true }); }
}
function apply(policy) {
  const current = JSON.stringify(policy);
  if (current !== fingerprint) {
    // Deny all forwarding while keys and Room memberships are changed. A
    // replaced key must never inherit access during reconciliation.
    applyFile('firewall.nft', nftPolicy({ peers: [], groups: [] }), 'nft', ['-f']);
    applyFile('wireguard.conf', wgPolicy(policy, privateKey, port), 'wg', ['syncconf', 'rpwg0']);
    applyFile('firewall.nft', nftPolicy(policy), 'nft', ['-f']);
    fingerprint = current;
    lastFirewallRefresh = Date.now();
  } else if (Date.now() - lastFirewallRefresh >= 5000) {
    applyFile('firewall.nft', nftPolicy(policy), 'nft', ['-f']);
    lastFirewallRefresh = Date.now();
  }
}

function stop() {
  if (!started) return;
  started = false;
  // Removing the interface before removing its firewall avoids a fail-open gap.
  let removed = false;
  try { run('ip', ['link', 'delete', 'rpwg0']); removed = true; } catch { /* retain the firewall */ }
  if (removed) try { run('nft', ['delete', 'table', 'inet', 'remote_print']); } catch { }
  fs.rmSync(scratch, { recursive: true, force: true });
}

try {
  run('ip', ['link', 'add', 'rpwg0', 'type', 'wireguard']);
  started = true;
  run('nft', ['add', 'table', 'inet', 'remote_print']);
  apply({ peers: [], groups: [] });
  run('ip', ['address', 'add', '10.247.0.1/16', 'dev', 'rpwg0']);
  run('ip', ['link', 'set', 'rpwg0', 'up']);
  console.log('Tunnel controller started with forwarding denied.');
} catch (error) {
  stop();
  throw error;
}

function tick() {
  let policy;
  try {
    const age = Date.now() - fs.statSync(heartbeatPath).mtimeMs;
    if (age < -5000 || age > 15000) throw new Error('Server heartbeat expired');
    const data = JSON.parse(fs.readFileSync(databasePath, 'utf8'));
    policy = tunnelPolicy(data.clients);
  } catch (error) {
    // No valid server state means no peers and no forwarding.
    policy = { peers: [], groups: [] };
    console.error(`Tunnel state unavailable: ${error.message}`);
  }
  try { apply(policy); }
  catch (error) {
    // If we cannot enforce a changed policy, remove the interface altogether.
    console.error(`Tunnel policy failed: ${error.message}`);
    stop();
    process.exit(1);
  }
}

process.on('SIGTERM', () => { stop(); process.exit(0); });
process.on('SIGINT', () => { stop(); process.exit(0); });
tick();
setInterval(tick, 1000);
