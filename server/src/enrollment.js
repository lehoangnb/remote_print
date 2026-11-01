import crypto from 'node:crypto';

export class EnrollmentInvites {
  constructor(now = () => Date.now()) { this.now = now; this.invites = new Map(); }
  create(serverUrl) {
    if (typeof serverUrl !== 'string' || serverUrl.length > 2048) throw new Error('INVALID_SERVER_URL');
    const url = new URL(serverUrl);
    if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) || url.username || url.password || url.search || url.hash)
      throw new Error('HTTPS_SERVER_URL_REQUIRED');
    for (const [id, invite] of this.invites) if (invite.expires <= this.now()) this.invites.delete(id);
    if (this.invites.size >= 128) throw new Error('TOO_MANY_INVITES');
    const id = crypto.randomBytes(16).toString('hex'), key = crypto.randomBytes(32).toString('base64url');
    const expires = this.now() + 300000;
    this.invites.set(id, { hash: this.hash(key), expires, binding: null });
    const payload = { version: 1, serverUrl: url.href.replace(/\/$/, ''), endpoint: `api/client/enroll/${id}`, key, expiresAt: new Date(expires).toISOString() };
    return { code: 'RPPAIR1.' + Buffer.from(JSON.stringify(payload)).toString('base64url'), expiresAt: payload.expiresAt };
  }
  hash(value) { return crypto.createHash('sha256').update(value).digest(); }
  claim(id, key, deviceId, secret) {
    const invite = this.invites.get(id);
    if (!invite || invite.expires <= this.now() || typeof key !== 'string' || key.length !== 43 || !crypto.timingSafeEqual(invite.hash, this.hash(key))) return false;
    const binding = this.hash(deviceId + ':' + secret).toString('hex');
    if (invite.binding && invite.binding !== binding) return false;
    invite.binding = binding; // Bind before any asynchronous registration; retries from this device are idempotent.
    return true;
  }
}
