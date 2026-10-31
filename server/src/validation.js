export const DEVICE_ID = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
export function cleanText(value, max) {
  return typeof value === 'string' ? value.trim().slice(0, max) : '';
}
export function validateRegistration(body) {
  const deviceId = cleanText(body.deviceId, 36), name = cleanText(body.name, 80), secret = cleanText(body.secret, 200);
  if (!DEVICE_ID.test(deviceId) || name.length < 2 || secret.length < 32 || !Array.isArray(body.printers) || body.printers.length > 100) return null;
  const printers = [...new Set(body.printers.map(x => cleanText(x, 200)).filter(Boolean))];
  const shares = body.windowsPrinterShares ?? {};
  if (!shares || typeof shares !== 'object' || Array.isArray(shares) || Object.keys(shares).length > printers.length) return null;
  const windowsPrinterShares = {};
  for (const [printer, share] of Object.entries(shares)) {
    if (!printers.includes(printer) || typeof share !== 'string' || share.length < 1 || share.length > 200 || share !== share.trim() ||
        ['.', '..'].includes(share) || /[\\/:*?"<>|\p{Cc}]/u.test(share)) return null;
    Object.defineProperty(windowsPrinterShares, printer, { value: share, enumerable: true });
  }
  return { deviceId, name, secret, printers, windowsPrinterShares, nativePrinting: body.nativePrinting === true };
}
