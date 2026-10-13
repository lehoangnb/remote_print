export const DEVICE_ID = /^[a-f0-9-]{36}$/i;

export function cleanText(value, max) {
  return typeof value === 'string' ? value.trim().slice(0, max) : '';
}

export function validateRegistration(body) {
  const deviceId = cleanText(body.deviceId, 36);
  const name = cleanText(body.name, 80);
  const secret = cleanText(body.secret, 200);
  const publicKey = cleanText(body.publicKey, 5000);
  const printers = Array.isArray(body.printers)
    ? [...new Set(body.printers.map((x) => cleanText(x, 200)).filter(Boolean))].slice(0, 100)
    : [];
  if (!DEVICE_ID.test(deviceId) || name.length < 2 || secret.length < 32 || !publicKey || !Array.isArray(body.printers)) return null;
  return { deviceId, name, secret, publicKey, printers };
}

