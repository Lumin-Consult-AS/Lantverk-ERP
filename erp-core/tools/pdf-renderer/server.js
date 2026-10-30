// PDF renderer for the ERP document service (ADR-0012).
//
//   POST /render   body: HTML (text/html)   ->  200 application/pdf
//   GET  /health                            ->  200 "ok"
//
// The HTML comes from tenant-defined templates, so the page is treated as untrusted: JavaScript is disabled and every
// network request is blocked (no fetching of internal addresses or tracking pixels). Only data: URLs and the document
// itself are allowed. Chromium is started once and reused; each render gets its own isolated browser context.

const http = require('http');
const { chromium } = require('playwright');

const PORT = Number(process.env.PORT || 3000);
const MAX_BODY_BYTES = Number(process.env.MAX_BODY_BYTES || 5 * 1024 * 1024);
const RENDER_TIMEOUT_MS = Number(process.env.RENDER_TIMEOUT_MS || 30000);
const MAX_CONCURRENT = Number(process.env.MAX_CONCURRENT || 4);

let browserPromise = null;
let active = 0;

function getBrowser() {
  if (!browserPromise) {
    browserPromise = chromium.launch({ args: ['--disable-dev-shm-usage'] }).catch((err) => {
      browserPromise = null;
      throw err;
    });
  }
  return browserPromise;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        reject(Object.assign(new Error('Request body too large'), { status: 413 }));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

async function renderPdf(html) {
  const browser = await getBrowser();
  const context = await browser.newContext({ javaScriptEnabled: false, offline: false });
  try {
    await context.route('**/*', (route) => {
      const url = route.request().url();
      return url.startsWith('data:') || url === 'about:blank' ? route.continue() : route.abort();
    });
    const page = await context.newPage();
    page.setDefaultTimeout(RENDER_TIMEOUT_MS);
    await page.setContent(html, { waitUntil: 'load' });
    return await page.pdf({ format: 'A4', printBackground: true, margin: { top: '15mm', bottom: '15mm', left: '15mm', right: '15mm' } });
  } finally {
    await context.close();
  }
}

const server = http.createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && req.url === '/health') {
      res.writeHead(200, { 'Content-Type': 'text/plain' }).end('ok');
      return;
    }

    if (req.method === 'POST' && req.url === '/render') {
      if (active >= MAX_CONCURRENT) {
        res.writeHead(503, { 'Retry-After': '2' }).end('Busy');
        return;
      }

      active += 1;
      try {
        const html = await readBody(req);
        if (!html.trim()) {
          res.writeHead(400).end('Empty body');
          return;
        }
        const pdf = await renderPdf(html);
        res.writeHead(200, { 'Content-Type': 'application/pdf', 'Content-Length': pdf.length }).end(pdf);
      } finally {
        active -= 1;
      }
      return;
    }

    res.writeHead(404).end();
  } catch (err) {
    const status = err.status || 500;
    console.error('Render failed:', err.message);
    if (!res.headersSent) {
      res.writeHead(status).end(status === 413 ? 'Request body too large' : 'Render failed');
    }
  }
});

server.listen(PORT, () => console.log(`PDF renderer listening on ${PORT}`));

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, async () => {
    server.close();
    if (browserPromise) {
      await (await browserPromise).close().catch(() => {});
    }
    process.exit(0);
  });
}
