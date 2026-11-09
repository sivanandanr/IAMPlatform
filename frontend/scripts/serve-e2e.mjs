// S1-10 (ADR-0019): serves the production portal build for Playwright under a strict Content Security Policy.
// Each response gets a fresh nonce, written into index.html's ngCspNonce placeholder so Angular (and AG Grid via
// CSP_NONCE) tag their <style> elements with it. No 'unsafe-inline', no 'unsafe-eval'. Test use only; listens on
// 127.0.0.1. Production hosting sets the same policy at the BFF/reverse proxy (see SECURITY_BASELINE).
import { randomBytes } from 'node:crypto';
import { readFile, stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('../dist/iam-portal/browser', import.meta.url)));
const port = Number(process.env.IAM_E2E_PORT ?? 4300);
const NONCE_PLACEHOLDER = 'IAM_CSP_NONCE';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

export function cspFor(nonce) {
  return [
    "default-src 'none'",
    `script-src 'self' 'nonce-${nonce}'`,
    `style-src 'self' 'nonce-${nonce}'`,
    "img-src 'self' data:",
    "font-src 'self'",
    "connect-src 'self'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
    "object-src 'none'",
    "require-trusted-types-for 'script'",
    'trusted-types angular angular#bundler',
  ].join('; ');
}

function securityHeaders(nonce) {
  return {
    'Content-Security-Policy': cspFor(nonce),
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Cross-Origin-Opener-Policy': 'same-origin',
    'Cache-Control': 'no-store',
    // SECURITY_BASELINE: no powerful browser features unless a screen needs one.
    'Permissions-Policy':
      'camera=(), microphone=(), geolocation=(), payment=(), usb=(), clipboard-read=(), interest-cohort=()',
  };
}

async function fileFor(urlPath) {
  const decoded = decodeURIComponent(urlPath.split('?')[0]);
  const candidate = resolve(join(root, normalize(decoded)));
  if (candidate !== root && !candidate.startsWith(root + sep)) {
    return null;
  }

  try {
    return (await stat(candidate)).isFile() ? candidate : null;
  } catch {
    return null;
  }
}

const server = createServer(async (req, res) => {
  const nonce = randomBytes(16).toString('base64');
  const headers = securityHeaders(nonce);
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.writeHead(405, { ...headers, Allow: 'GET, HEAD' }).end();
    return;
  }

  let path;
  try {
    path = (await fileFor(req.url ?? '/')) ?? join(root, 'index.html');
  } catch {
    res.writeHead(400, headers).end();
    return;
  }

  const type = TYPES[extname(path)] ?? 'application/octet-stream';
  let body = await readFile(path);
  if (path.endsWith('index.html')) {
    body = body.toString('utf8').replaceAll(NONCE_PLACEHOLDER, nonce);
  }

  res.writeHead(200, { ...headers, 'Content-Type': type });
  res.end(req.method === 'HEAD' ? undefined : body);
});

// Imported by the browser tests for the expected policy; only listen when run as the server.
if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  server.listen(port, '127.0.0.1', () => console.log(`e2e server on http://127.0.0.1:${port}`));
}
