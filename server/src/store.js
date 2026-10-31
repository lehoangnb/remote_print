import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import bcrypt from 'bcryptjs';

function hashDeviceSecret(secret) {
  return `sha256:${crypto.createHash('sha256').update(secret, 'utf8').digest('hex')}`;
}

async function verifyDeviceSecret(secret, storedHash) {
  if (storedHash.startsWith('sha256:')) {
    const expected = Buffer.from(storedHash.slice(7), 'hex');
    const actual = Buffer.from(hashDeviceSecret(secret).slice(7), 'hex');
    return expected.length === actual.length && crypto.timingSafeEqual(expected, actual);
  }
  return bcrypt.compare(secret, storedHash);
}

export class Store {
  constructor(dataDir, tunnelStateDir = null) {
    this.dataDir = path.resolve(dataDir);
    this.dbPath = path.join(this.dataDir, 'db.json');
    this.auditPath = path.join(this.dataDir, 'audit.log');
    this.tunnelStateDir = tunnelStateDir ? path.resolve(tunnelStateDir) : null;
    fs.mkdirSync(this.dataDir, { recursive: true });
    if (this.tunnelStateDir) fs.mkdirSync(this.tunnelStateDir, { recursive: true });
    this.data = this.#load();
    this.lastSeenPersisted = new Map(this.data.clients.map((client) => [client.id, Date.parse(client.lastSeenAt) || 0]));
    this.#save();
  }

  #load() {
    if (!fs.existsSync(this.dbPath)) return { schemaVersion: 2, clients: [], rooms: ['Default'] };
    const parsed = JSON.parse(fs.readFileSync(this.dbPath, 'utf8'));
    const clients = (parsed.clients ?? []).map(client => {
      const { publicKey, printerCapabilities, printerFeatures, ...device } = client;
      return device;
    });
    if (parsed.schemaVersion !== 2 && (parsed.jobs?.length || (parsed.clients ?? []).some(c => c.publicKey))) {
      // Preserve pre-upgrade metadata once; no active document/job processing remains.
      const backup = path.join(this.dataDir, 'db-before-network-only.json');
      if (!fs.existsSync(backup)) fs.copyFileSync(this.dbPath, backup, fs.constants.COPYFILE_EXCL);
    }
    const rooms = [...new Set([
      ...(parsed.rooms ?? []),
      ...clients.map((x) => x.room).filter(Boolean)
    ])];
    // An explicitly saved empty list must remain empty after restart.
    if (rooms.length === 0 && !Array.isArray(parsed.rooms)) rooms.push('Default');
    return { schemaVersion: 2, clients, rooms };
  }

  #save() {
    const temp = `${this.dbPath}.tmp`;
    fs.writeFileSync(temp, JSON.stringify(this.data, null, 2));
    fs.renameSync(temp, this.dbPath);
    this.writeTunnelState();
  }

  writeTunnelState() {
    if (!this.tunnelStateDir) return;
    // The privileged relay only needs routing identities and Room membership.
    // The legacy network controller receives only authorized tunnel metadata.
    const clients = this.data.clients.filter((client) => client.status === 'approved' && client.room &&
      client.tunnelAddress && client.tunnelPublicKey).map((client) => ({
      status: client.status, room: client.room, lastSeenAt: client.lastSeenAt,
      tunnelAddress: client.tunnelAddress, tunnelPublicKey: client.tunnelPublicKey
    }));
    const target = path.join(this.tunnelStateDir, 'state.json');
    const temporary = `${target}.tmp`;
    fs.writeFileSync(temporary, JSON.stringify({ clients }));
    fs.renameSync(temporary, target);
  }

  audit(action, details = {}) {
    fs.appendFileSync(this.auditPath, `${JSON.stringify({ at: new Date().toISOString(), action, ...details })}\n`);
  }

  async registerClient(input) {
    const now = new Date().toISOString();
    const existing = this.data.clients.find((x) => x.id === input.deviceId);
    if (existing) {
      if (!(await verifyDeviceSecret(input.secret, existing.secretHash))) throw new Error('DEVICE_AUTH_FAILED');
      if (!existing.secretHash.startsWith('sha256:')) existing.secretHash = hashDeviceSecret(input.secret);
      existing.name = input.name;
      existing.printers = input.printers;
      existing.windowsPrinterShares = input.windowsPrinterShares ?? {};
      existing.nativePrinting = input.nativePrinting === true;
      existing.lastSeenAt = now;
      this.#save();
      return this.publicClient(existing);
    }

    const client = {
      id: input.deviceId,
      name: input.name,
      secretHash: hashDeviceSecret(input.secret),
      printers: input.printers,
      windowsPrinterShares: input.windowsPrinterShares ?? {},
      nativePrinting: input.nativePrinting === true,
      status: 'pending',
      room: null,
      createdAt: now,
      lastSeenAt: now
    };
    this.data.clients.push(client);
    this.#save();
    this.lastSeenPersisted.set(client.id, Date.parse(now));
    this.audit('client_registered', { clientId: client.id, name: client.name });
    return this.publicClient(client);
  }

  async authenticateClient(deviceId, secret) {
    const client = this.data.clients.find((x) => x.id === deviceId);
    if (!client || !secret || !(await verifyDeviceSecret(secret, client.secretHash))) return null;
    const now = new Date();
    const shouldPersist = now.getTime() - (this.lastSeenPersisted.get(client.id) ?? 0) >= 15_000;
    client.lastSeenAt = now.toISOString();
    if (shouldPersist) {
      this.#save();
      this.lastSeenPersisted.set(client.id, now.getTime());
    }
    return client;
  }

  publicClient(client) {
    return {
      id: client.id,
      name: client.name,
      printers: client.printers,
      windowsPrinterShares: client.windowsPrinterShares ?? {},
      nativePrinting: client.nativePrinting === true,
      status: client.status,
      room: client.room,
      createdAt: client.createdAt,
      lastSeenAt: client.lastSeenAt
    };
  }

  listClients() {
    return this.data.clients.map((x) => this.adminClient(x));
  }

  adminClient(client) {
    return { ...this.publicClient(client), tunnel: {
      enrolled: Boolean(client.tunnelPublicKey && client.tunnelAddress),
      address: client.tunnelAddress ?? null
    } };
  }

  listRooms() {
    return [...this.data.rooms].sort((a, b) => a.localeCompare(b));
  }

  createRoom(name) {
    const existing = this.data.rooms.find((x) => x.toLocaleLowerCase() === name.toLocaleLowerCase());
    if (existing) return existing;
    this.data.rooms.push(name);
    this.#save();
    this.audit('room_created', { room: name });
    return name;
  }

  deleteRoom(name) {
    if (!this.data.rooms.includes(name)) return false;
    // Never remove a Room while any client is assigned to it.
    if (this.data.clients.some((client) => client.room === name)) throw new Error('ROOM_IN_USE');
    this.data.rooms = this.data.rooms.filter((room) => room !== name);
    this.#save();
    this.audit('room_deleted', { room: name });
    return true;
  }

  approveClient(id, room) {
    const client = this.data.clients.find((x) => x.id === id);
    if (!client) return null;
    const existingRoom = this.data.rooms.find((x) => x.toLocaleLowerCase() === room.toLocaleLowerCase());
    const canonicalRoom = existingRoom ?? room;
    client.status = 'approved';
    client.room = canonicalRoom;
    if (!existingRoom) this.data.rooms.push(canonicalRoom);
    this.#save();
    this.audit('client_approved', { clientId: id, room: canonicalRoom });
    return this.adminClient(client);
  }

  rejectClient(id) {
    const client = this.data.clients.find((x) => x.id === id);
    if (!client) return null;
    client.status = 'rejected';
    client.room = null;
    this.#save();
    this.audit('client_rejected', { clientId: id });
    return this.adminClient(client);
  }

  peersFor(client) {
    if (client.status !== 'approved' || !client.room) return [];
    return this.data.clients
      .filter((x) => x.id !== client.id && x.status === 'approved' && x.room === client.room)
      .map((x) => this.publicClient(x));
  }

  // Addresses remain reserved across rejection and restart, so a stale peer can
  // never acquire a different device's identity by recycling its address.
  enrollTunnel(client, publicKey) {
    if (this.data.clients.some((other) => other.id !== client.id && other.tunnelPublicKey === publicKey))
      throw new Error('TUNNEL_KEY_IN_USE');
    if (!client.tunnelAddress) {
      const used = new Set(this.data.clients.map((other) => other.tunnelAddress).filter(Boolean));
      let address;
      for (let n = 2; n <= 65534; n++) {
        const candidate = `10.247.${n >> 8}.${n & 255}`;
        if (!used.has(candidate)) { address = candidate; break; }
      }
      if (!address) throw new Error('TUNNEL_ADDRESS_EXHAUSTED');
      client.tunnelAddress = address;
    }
    client.tunnelPublicKey = publicKey;
    this.#save();
    return client.tunnelAddress;
  }

  tunnelPeersFor(client) {
    if (client.status !== 'approved' || !client.room) return [];
    return this.data.clients.filter((peer) => peer.id !== client.id && peer.status === 'approved' &&
      peer.room === client.room && peer.tunnelPublicKey && peer.tunnelAddress)
      .map((peer) => ({ id: peer.id, address: peer.tunnelAddress }));
  }

}
