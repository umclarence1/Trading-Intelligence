const fs = require('fs');
const path = require('path');

// Vercel (or any CI) should set an environment variable named BACKEND_URL
const backend = process.env.BACKEND_URL || '';
const outDir = path.join(__dirname, '..', 'wwwroot');
const outFile = path.join(outDir, 'env.js');

if (!fs.existsSync(outDir)) fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(outFile, `window.__BACKEND_URL__ = ${JSON.stringify(backend)};\n`);
console.log('Wrote', outFile, 'with value:', backend);
