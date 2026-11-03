import fs from 'node:fs';
import path from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { inflateRawSync } from 'node:zlib';

const maxSize = 256 * 1024 * 1024;
const runtimes = new Set(['win-x64', 'win-arm64']);
export const validVersion = value => typeof value === 'string' && /^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){2,3}$/.test(value) && value.split('.').every(n => Number(n) <= 65534);
const compare = (a, b) => {
  const x = a.split('.').map(Number), y = b.split('.').map(Number);
  for (let i = 0; i < 4; i++) { const d = (x[i] || 0) - (y[i] || 0); if (d) return d; }
  return 0;
};
function invalid(code, status = 400) { return Object.assign(new Error(code), { status }); }

// Read only the central directory and small build manifest, never unpack uploaded code.
async function inspectPackage(file, size, runtime) {
  const handle = await fs.promises.open(file, 'r');
  try {
    async function read(offset, length) {
      if (offset < 0 || length < 0 || offset + length > size) throw invalid('INVALID_UPDATE_PACKAGE');
      const buffer = Buffer.alloc(length);
      if ((await handle.read(buffer, 0, length, offset)).bytesRead !== length) throw invalid('INVALID_UPDATE_PACKAGE');
      return buffer;
    }
    const tail = await read(Math.max(0, size - 65557), Math.min(size, 65557));
    let end = -1;
    for (let i = tail.length - 22; i >= 0; i--) {
      if (tail.readUInt32LE(i) === 0x06054b50 && i + 22 + tail.readUInt16LE(i + 20) === tail.length) { end = i; break; }
    }
    if (end < 0 || tail.readUInt32LE(end + 4) !== 0 || tail.readUInt16LE(end + 8) !== tail.readUInt16LE(end + 10)) throw invalid('INVALID_UPDATE_PACKAGE');
    const length = tail.readUInt32LE(end + 12), offset = tail.readUInt32LE(end + 16);
    if (length > 4 * 1024 * 1024) throw invalid('INVALID_UPDATE_PACKAGE');
    const directory = await read(offset, length), entries = new Map();
    for (let p = 0; p < directory.length;) {
      if (p + 46 > directory.length || directory.readUInt32LE(p) !== 0x02014b50) throw invalid('INVALID_UPDATE_PACKAGE');
      const nameLength = directory.readUInt16LE(p + 28), extra = directory.readUInt16LE(p + 30), comment = directory.readUInt16LE(p + 32);
      if (p + 46 + nameLength + extra + comment > directory.length) throw invalid('INVALID_UPDATE_PACKAGE');
      const name = directory.subarray(p + 46, p + 46 + nameLength).toString('utf8').replaceAll('\\', '/');
      if (name.startsWith('/') || name.includes(':') || name.split('/').includes('..') || entries.has(name.toLowerCase())) throw invalid('INVALID_UPDATE_PACKAGE');
      entries.set(name.toLowerCase(), { flags: directory.readUInt16LE(p + 8), method: directory.readUInt16LE(p + 10), packed: directory.readUInt32LE(p + 20), unpacked: directory.readUInt32LE(p + 24), offset: directory.readUInt32LE(p + 42) });
      p += 46 + nameLength + extra + comment;
    }
    for (const required of ['RemotePrint.Client.exe', 'RemotePrint.Client.dll', 'Service/RemotePrint.Service.exe', 'Service/RemotePrint.Service.dll', 'translations.json', 'build-info.json'])
      if (!entries.has(required.toLowerCase())) throw invalid('INVALID_UPDATE_PACKAGE');
    const entry = entries.get('build-info.json');
    if (entry.flags & 1 || entry.packed > 1024 * 1024 || entry.unpacked > 1024 * 1024) throw invalid('INVALID_UPDATE_PACKAGE');
    const local = await read(entry.offset, 30);
    if (local.readUInt32LE(0) !== 0x04034b50) throw invalid('INVALID_UPDATE_PACKAGE');
    const packed = await read(entry.offset + 30 + local.readUInt16LE(26) + local.readUInt16LE(28), entry.packed);
    const bytes = entry.method === 0 ? packed : entry.method === 8 ? inflateRawSync(packed, { maxOutputLength: 1024 * 1024 }) : null;
    const manifest = bytes && JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
    if (manifest?.protocol !== 'native-raw-v1' || !validVersion(manifest.version) || manifest.runtime !== runtime) throw invalid('INVALID_UPDATE_PACKAGE');
    return manifest.version;
  } catch (e) { if (e.status) throw e; throw invalid('INVALID_UPDATE_PACKAGE'); }
  finally { await handle.close(); }
}

export function attachClientUpdates(app, { dataDir, adminAuth, audit }) {
  const root = path.resolve(dataDir, 'client-updates');
  fs.mkdirSync(root, { recursive: true });
  const busy = new Set();
  const pendingPackages = new Set();
  const activeDownloads = new Map();
  const deferredDeletes = new Set();
  const manifestPath = runtime => path.join(root, `${runtime}.json`);
  const latest = runtime => {
    try { return JSON.parse(fs.readFileSync(manifestPath(runtime), 'utf8')); }
    catch (e) { if (e.code === 'ENOENT') return null; throw e; }
  };
  const currentPackages = (skipDamaged = false) => {
    const names = new Set();
    for (const runtime of runtimes) {
      try {
        const release = latest(runtime);
        if (!release) continue;
        if (release.runtime !== runtime || !/^[a-f0-9]{64}$/.test(release.sha256))
          throw new Error(`Invalid client update manifest for ${runtime}`);
        names.add(`${release.sha256}.zip`);
      } catch (e) { if (!skipDamaged) throw e; }
    }
    return names;
  };
  const cleanupOldPackages = () => {
    // Keep the current ZIP for each Windows architecture. Protect packages being
    // published or downloaded while the other architecture is updated.
    let keep;
    try { keep = currentPackages(); }
    catch (e) { console.warn('Skipping client update cleanup:', e); return; }
    let files;
    try { files = fs.readdirSync(root); }
    catch (e) { console.warn('Could not list client update packages for cleanup:', e); return; }
    for (const name of files) {
      if (!/^[a-f0-9]{64}\.zip$/.test(name)) continue;
      if (keep.has(name) || pendingPackages.has(name)) { deferredDeletes.delete(name); continue; }
      if (activeDownloads.has(name)) { deferredDeletes.add(name); continue; }
      try { fs.unlinkSync(path.join(root, name)); deferredDeletes.delete(name); }
      catch (e) { console.warn(`Could not delete obsolete client update ${name}:`, e); }
    }
  };
  // Also remove packages left by earlier server versions on the first startup.
  cleanupOldPackages();
  app.use('/api/client', (req, res, next) => {
    const version = req.header('x-client-version'), runtime = req.header('x-client-runtime');
    if (validVersion(version) && runtimes.has(runtime)) {
      // Commands travel with normal API replies; clients never need an update polling timer.
      res.set('Cache-Control', 'no-store');
      let release;
      try { release = latest(runtime); }
      catch { return next(); } // A damaged release manifest must not interrupt normal client traffic.
      const difference = release && validVersion(release.version) ? compare(version, release.version) : null;
      if (difference !== null && (difference < 0 || (difference === 0 && release.forceUpdate === true &&
          release.releaseId && req.header('x-client-update-id') !== release.releaseId))) {
        // Omit release notes to keep the response header small even with 4000-character notes.
        const { version, runtime, size, sha256, downloadPath, forceUpdate = false, releaseId = '' } = release;
        res.set('x-client-update', Buffer.from(JSON.stringify({ version, runtime, size, sha256, downloadPath, notes: '', forceUpdate, releaseId })).toString('base64'));
      }
    }
    next();
  });
  app.get('/api/admin/updates', adminAuth, (_req, res) => res.set('Cache-Control', 'no-store').json([...runtimes].map(latest).filter(Boolean)));
  app.put('/api/admin/updates/:runtime', adminAuth, async (req, res, next) => {
    const { runtime } = req.params, { notes = '', forceUpdate = 'false' } = req.query;
    if (!runtimes.has(runtime) || typeof notes !== 'string' || notes.length > 4000 ||
        !['true', 'false'].includes(forceUpdate) || !req.is('application/zip'))
      return res.status(400).json({ error: 'INVALID_UPDATE_METADATA' });
    if (busy.has(runtime)) return res.status(409).json({ error: 'UPDATE_UPLOAD_BUSY' });
    busy.add(runtime);
    const temp = path.join(root, `${randomUUID()}.upload`);
    let metaTemp, packageName;
    try {
      const previous = latest(runtime);
      let size = 0;
      const hash = createHash('sha256');
      await pipeline(req, new Transform({ transform(chunk, _encoding, callback) {
        size += chunk.length;
        if (size > maxSize) return callback(invalid('UPDATE_TOO_LARGE', 413));
        hash.update(chunk); callback(null, chunk);
      } }), fs.createWriteStream(temp, { flags: 'wx' }));
      const version = await inspectPackage(temp, size, runtime);
      if (previous && compare(version, previous.version) < 0) throw invalid('UPDATE_VERSION_MUST_NOT_DECREASE', 409);
      const sha256 = hash.digest('hex');
      const release = { version, runtime, notes, size, sha256, forceUpdate: forceUpdate === 'true', releaseId: randomUUID(),
        publishedAt: new Date().toISOString(), downloadPath: `/api/client/updates/packages/${sha256}.zip` };
      packageName = `${sha256}.zip`;
      pendingPackages.add(packageName);
      // Re-uploading identical bytes only refreshes the release metadata.
      // Re-uploading a changed ZIP with the same version replaces the old hash.
      try { await fs.promises.access(path.join(root, packageName)); }
      catch (e) {
        if (e.code !== 'ENOENT') throw e;
        await fs.promises.rename(temp, path.join(root, packageName));
      }
      metaTemp = path.join(root, `${randomUUID()}.json.tmp`);
      await fs.promises.writeFile(metaTemp, JSON.stringify(release), { flag: 'wx' });
      await fs.promises.rename(metaTemp, manifestPath(runtime));
      pendingPackages.delete(packageName);
      cleanupOldPackages();
      audit('client_update_published', { version, runtime, sha256, forceUpdate: release.forceUpdate, releaseId: release.releaseId });
      res.status(previous && compare(version, previous.version) === 0 ? 200 : 201).json(release);
    } catch (e) {
      if (e.status && !res.headersSent && !res.destroyed) res.status(e.status).json({ error: e.message });
      else if (!e.status) next(e);
    } finally {
      busy.delete(runtime);
      await fs.promises.rm(temp, { force: true });
      if (metaTemp) await fs.promises.rm(metaTemp, { force: true });
      if (packageName) pendingPackages.delete(packageName);
      cleanupOldPackages();
    }
  });
  // These contain public release binaries only, never device keys or configuration.
  app.get('/api/client/updates/:runtime', (req, res) => {
    if (!runtimes.has(req.params.runtime)) return res.status(400).json({ error: 'INVALID_UPDATE_METADATA' });
    const release = latest(req.params.runtime);
    res.set('Cache-Control', 'no-store');
    return release ? res.json(release) : res.sendStatus(204);
  });
  app.get('/api/client/updates/packages/:name', (req, res) => {
    if (!/^[a-f0-9]{64}\.zip$/.test(req.params.name)) return res.sendStatus(404);
    // Retired packages are no longer offered to new download requests.
    let current;
    current = currentPackages(true);
    if (!current.has(req.params.name)) return res.sendStatus(404);
    activeDownloads.set(req.params.name, (activeDownloads.get(req.params.name) || 0) + 1);
    let released = false;
    const releaseDownload = () => {
      if (released) return;
      released = true;
      const count = activeDownloads.get(req.params.name);
      if (count === 1) {
        activeDownloads.delete(req.params.name);
        if (deferredDeletes.has(req.params.name)) cleanupOldPackages();
      } else activeDownloads.set(req.params.name, count - 1);
    };
    res.once('finish', releaseDownload);
    res.once('close', releaseDownload);
    res.sendFile(path.join(root, req.params.name), { headers: { 'Cache-Control': 'public, max-age=31536000, immutable', 'Content-Type': 'application/zip' } }, err => {
      if (err && !res.headersSent) res.sendStatus(err.statusCode === 404 ? 404 : 500);
    });
  });
}
