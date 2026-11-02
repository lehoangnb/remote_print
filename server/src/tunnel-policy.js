// The relay operates in a dedicated network namespace. This module accepts only
// addresses allocated by the control plane and never interpolates client text
// (including Room names) into nft or wg configuration.
import { roomsFor } from './rooms.js';

export function isTunnelKey(key) {
  if (typeof key !== 'string' || !/^[A-Za-z0-9+/]{43}=$/u.test(key)) return false;
  const bytes = Buffer.from(key, 'base64');
  return bytes.length === 32 && bytes.toString('base64') === key && bytes.some((x) => x !== 0);
}

export function isTunnelAddress(address) {
  if (typeof address !== 'string') return false;
  const match = /^10\.247\.(0|[1-9]\d{0,2})\.(0|[1-9]\d{0,2})$/u.exec(address);
  if (!match) return false;
  const high = Number(match[1]), low = Number(match[2]);
  const index = high * 256 + low;
  return high <= 255 && low <= 255 && index >= 2 && index <= 65534;
}

export function tunnelPolicy(clients, now = Date.now()) {
  if (!Array.isArray(clients)) throw new Error('Invalid client list');
  const keys = new Set(), addresses = new Set(), rooms = new Map(), peers = [];
  for (const client of clients) {
    if (client.status !== 'approved' || !roomsFor(client).length || !client.tunnelAddress || !client.tunnelPublicKey) continue;
    const lastSeen = Date.parse(client.lastSeenAt);
    // Clients heartbeat independently every 10 s; the API publishes the relay
    // snapshot every 3 s. Allow a modest delay without retaining dead peers
    // for a full minute after their final authenticated API request.
    if (!Number.isFinite(lastSeen) || lastSeen > now + 5000 || now - lastSeen > 30_000) continue;
    if (roomsFor(client).some(room => typeof room !== 'string' || !room) || !isTunnelAddress(client.tunnelAddress) ||
        !isTunnelKey(client.tunnelPublicKey) || keys.has(client.tunnelPublicKey) || addresses.has(client.tunnelAddress))
      throw new Error('Invalid or duplicate tunnel peer');
    keys.add(client.tunnelPublicKey);
    addresses.add(client.tunnelAddress);
    peers.push({ address: client.tunnelAddress, publicKey: client.tunnelPublicKey });
    for (const room of new Set(roomsFor(client))) {
      if (!rooms.has(room)) rooms.set(room, []);
      rooms.get(room).push(client.tunnelAddress);
    }
  }
  peers.sort((a, b) => a.address.localeCompare(b.address));
  const groups = [...rooms.values()].map((group) => group.sort()).filter((group) => group.length > 1)
    .sort((a, b) => a[0].localeCompare(b[0]));
  return { peers, groups };
}

export function nftPolicy(policy) {
  const lines = [
    'delete table inet remote_print',
    'add table inet remote_print',
    'add chain inet remote_print forward { type filter hook forward priority -10; policy accept; }'
  ];
  policy.groups.forEach((group, index) => {
    // Elements expire if the controller stops refreshing policy, so a hung
    // process cannot leave an old authorization active indefinitely.
    lines.push(`add set inet remote_print room${index} { type ipv4_addr; flags timeout; timeout 15s; elements = { ${group.join(', ')} }; }`);
    const room = `iifname "rpwg0" oifname "rpwg0" ip saddr @room${index} ip daddr @room${index}`;
    lines.push(`add rule inet remote_print forward ${room} ct state established accept`);
    lines.push(`add rule inet remote_print forward ${room} tcp dport { 135, 445, 49152-65535 } ct state new accept`);
  });
  // Drop all traffic entering or leaving the tunnel, including forwarding to
  // the public interface; the preceding rules are the only exceptions.
  lines.push('add rule inet remote_print forward iifname "rpwg0" drop');
  lines.push('add rule inet remote_print forward oifname "rpwg0" drop');
  lines.push('add chain inet remote_print input { type filter hook input priority -10; policy accept; }');
  lines.push('add rule inet remote_print input iifname "rpwg0" drop');
  return lines.join('\n') + '\n';
}

export function wgPolicy(policy, privateKey, listenPort) {
  if (!isTunnelKey(privateKey) || !Number.isInteger(listenPort) || listenPort < 1 || listenPort > 65535)
    throw new Error('Invalid relay key or port');
  return `[Interface]\nPrivateKey = ${privateKey}\nListenPort = ${listenPort}\n` +
    policy.peers.map((peer) => `\n[Peer]\nPublicKey = ${peer.publicKey}\nAllowedIPs = ${peer.address}/32\n`).join('');
}
