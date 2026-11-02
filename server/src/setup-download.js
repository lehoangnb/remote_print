import fs from 'node:fs/promises';

// A bounded UTF-8 JSON overlay followed by uint32-LE length and this marker.
// Windows loads the original PE as usual; setup reads only its own final overlay.
export const setupConfigMarker = Buffer.from('REMOTEPRINT-SETUP-CONFIG-V1', 'ascii');

export function setupServerUrl(value) {
  if (typeof value !== 'string' || value.length > 2048 || /[\s\\]/u.test(value)) throw new Error('INVALID_PUBLIC_SERVER_URL');
  const url = new URL(value);
  const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127\./u.test(url.hostname);
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && loopback)) ||
      url.username || url.password || url.pathname !== '/' || url.search || url.hash)
    throw new Error('INVALID_PUBLIC_SERVER_URL');
  return url.origin;
}

export function attachSetupDownload(app, { setupFile, publicServerUrl = '' }) {
  // A bad deployment setting fails at startup, rather than serving unusable installers.
  const canonical = publicServerUrl ? setupServerUrl(publicServerUrl) : null;
  app.get('/downloads/RemotePrint.Setup.exe', async (req, res, next) => {
    let serverUrl;
    try { serverUrl = canonical || setupServerUrl(`${req.protocol}://${req.host}`); }
    catch { return res.status(400).json({ error: 'INVALID_PUBLIC_SERVER_URL' }); }
    try {
      const executable = await fs.readFile(setupFile);
      if (executable.length < 2 || executable.length > 2 * 1024 * 1024 || executable.toString('ascii', 0, 2) !== 'MZ')
        throw new Error('INVALID_SETUP_EXECUTABLE');
      const config = Buffer.from(JSON.stringify({ serverUrl }), 'utf8');
      const length = Buffer.alloc(4); length.writeUInt32LE(config.length);
      res.set({ 'Content-Type': 'application/octet-stream', 'Cache-Control': 'private, no-store',
        'Content-Disposition': 'attachment; filename="RemotePrint.Setup.exe"' });
      res.send(Buffer.concat([executable, config, length, setupConfigMarker]));
    } catch (error) {
      if (error.code === 'ENOENT') return res.status(503).json({ error: 'SETUP_NOT_BUILT' });
      next(error);
    }
  });
}
