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
  constructor(dataDir) {
    this.dataDir = path.resolve(dataDir);
    this.jobsDir = path.join(this.dataDir, 'jobs');
    this.dbPath = path.join(this.dataDir, 'db.json');
    this.auditPath = path.join(this.dataDir, 'audit.log');
    fs.mkdirSync(this.jobsDir, { recursive: true });
    this.data = this.#load();
  }

  #load() {
    if (!fs.existsSync(this.dbPath)) return { clients: [], jobs: [], rooms: ['Default'] };
    const parsed = JSON.parse(fs.readFileSync(this.dbPath, 'utf8'));
    const clients = parsed.clients ?? [];
    const rooms = [...new Set([
      ...(parsed.rooms ?? []),
      ...clients.map((x) => x.room).filter(Boolean)
    ])];
    // An explicitly saved empty list must remain empty after restart.
    if (rooms.length === 0 && !Array.isArray(parsed.rooms)) rooms.push('Default');
    return { clients, jobs: parsed.jobs ?? [], rooms };
  }

  #save() {
    const temp = `${this.dbPath}.tmp`;
    fs.writeFileSync(temp, JSON.stringify(this.data, null, 2));
    fs.renameSync(temp, this.dbPath);
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
      existing.publicKey = input.publicKey;
      existing.printers = input.printers;
      existing.lastSeenAt = now;
      this.#save();
      return this.publicClient(existing);
    }

    const client = {
      id: input.deviceId,
      name: input.name,
      secretHash: hashDeviceSecret(input.secret),
      publicKey: input.publicKey,
      printers: input.printers,
      status: 'pending',
      room: null,
      createdAt: now,
      lastSeenAt: now
    };
    this.data.clients.push(client);
    this.#save();
    this.audit('client_registered', { clientId: client.id, name: client.name });
    return this.publicClient(client);
  }

  async authenticateClient(deviceId, secret) {
    const client = this.data.clients.find((x) => x.id === deviceId);
    if (!client || !secret || !(await verifyDeviceSecret(secret, client.secretHash))) return null;
    const now = new Date();
    const shouldPersist = now.getTime() - new Date(client.lastSeenAt).getTime() >= 15_000;
    client.lastSeenAt = now.toISOString();
    if (shouldPersist) this.#save();
    return client;
  }

  publicClient(client) {
    return {
      id: client.id,
      name: client.name,
      publicKey: client.publicKey,
      printers: client.printers,
      status: client.status,
      room: client.room,
      createdAt: client.createdAt,
      lastSeenAt: client.lastSeenAt
    };
  }

  listClients() {
    return this.data.clients.map((x) => this.publicClient(x));
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
    client.status = 'approved';
    client.room = room;
    if (!this.data.rooms.some((x) => x.toLocaleLowerCase() === room.toLocaleLowerCase()))
      this.data.rooms.push(room);
    this.#save();
    this.audit('client_approved', { clientId: id, room });
    return this.publicClient(client);
  }

  rejectClient(id) {
    const client = this.data.clients.find((x) => x.id === id);
    if (!client) return null;
    client.status = 'rejected';
    client.room = null;
    this.#save();
    this.audit('client_rejected', { clientId: id });
    return this.publicClient(client);
  }

  peersFor(client) {
    if (client.status !== 'approved' || !client.room) return [];
    return this.data.clients
      .filter((x) => x.id !== client.id && x.status === 'approved' && x.room === client.room)
      .map((x) => this.publicClient(x));
  }

  createJob(sender, input, payload) {
    const target = this.data.clients.find((x) => x.id === input.targetClientId);
    if (!target || target.status !== 'approved' || target.room !== sender.room) throw new Error('TARGET_NOT_IN_ROOM');
    if (!target.printers.includes(input.printerName)) throw new Error('PRINTER_NOT_SHARED');

    const id = crypto.randomUUID();
    const now = new Date().toISOString();
    const payloadPath = path.join(this.jobsDir, `${id}.json`);
    fs.writeFileSync(payloadPath, JSON.stringify(payload));
    const job = {
      id,
      senderId: sender.id,
      senderName: sender.name,
      targetClientId: target.id,
      targetName: target.name,
      room: sender.room,
      printerName: input.printerName,
      fileName: input.fileName,
      contentType: input.contentType,
      encryptedBytes: Buffer.byteLength(payload.ciphertext, 'base64'),
      status: 'pending',
      error: null,
      createdAt: now,
      updatedAt: now,
      payloadPath
    };
    this.data.jobs.push(job);
    this.#save();
    this.audit('job_created', { jobId: id, senderId: sender.id, targetClientId: target.id, printerName: input.printerName });
    return this.publicJob(job);
  }

  publicJob(job) {
    const { payloadPath, ...safe } = job;
    return safe;
  }

  pendingJobs(client) {
    return this.data.jobs
      .filter((x) => x.targetClientId === client.id && ['pending', 'processing'].includes(x.status))
      .map((x) => this.publicJob(x));
  }

  getJobForTarget(client, id) {
    const job = this.data.jobs.find((x) => x.id === id && x.targetClientId === client.id);
    if (!job || !fs.existsSync(job.payloadPath)) return null;
    job.status = 'processing';
    job.updatedAt = new Date().toISOString();
    this.#save();
    return { job: this.publicJob(job), payload: JSON.parse(fs.readFileSync(job.payloadPath, 'utf8')) };
  }

  updateJob(client, id, status, error) {
    const job = this.data.jobs.find((x) => x.id === id && x.targetClientId === client.id);
    if (!job) return null;
    job.status = status;
    job.error = error ?? null;
    job.updatedAt = new Date().toISOString();
    this.#save();
    this.audit('job_status', { jobId: id, targetClientId: client.id, status, error: job.error });
    return this.publicJob(job);
  }

  listJobs() {
    return [...this.data.jobs].reverse().slice(0, 250).map((x) => this.publicJob(x));
  }

  cleanup(retentionDays) {
    const cutoff = Date.now() - retentionDays * 86400000;
    const keep = [];
    for (const job of this.data.jobs) {
      const expired = ['completed', 'failed'].includes(job.status) && new Date(job.updatedAt).getTime() < cutoff;
      if (expired) {
        if (fs.existsSync(job.payloadPath)) fs.unlinkSync(job.payloadPath);
      } else keep.push(job);
    }
    if (keep.length !== this.data.jobs.length) {
      this.data.jobs = keep;
      this.#save();
    }
  }
}

