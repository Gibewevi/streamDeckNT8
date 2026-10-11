export const PRODUCTION_URL = 'https://bitlearn.fr';

/** A device credential must never be sent to an arbitrary host or to unencrypted remote HTTP. */
export function normalizeServerUrl(value: unknown): string | null {
  if (typeof value !== 'string' || !value.trim()) return null;
  try {
    const url = new URL(value.trim());
    if (url.username || url.password) return null;
    if (url.origin === PRODUCTION_URL || url.origin === 'https://dev.bitlearn.fr') return url.origin;
    if (url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)) return url.origin;
  } catch { /* A malformed preference must not stop offline trading. */ }
  return null;
}

export function resolveServerTarget(options: {
  packagedTarget?: string | null;
  argument?: string;
  environment?: string;
  configured?: string;
}): string {
  // A downloaded release is tied to its environment even if a development override survives.
  if (options.packagedTarget) return normalizeServerUrl(options.packagedTarget) || PRODUCTION_URL;
  return normalizeServerUrl(options.argument || options.environment || options.configured) || PRODUCTION_URL;
}
