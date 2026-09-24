'use strict';
// Generates icon.png (512), icon.ico (multi-size) and tray.png (32) from an
// SVG shield source. Zero deps: PNG encoded by hand (zlib is built into Node).
const zlib = require('node:zlib');
const fs = require('node:fs');
const path = require('node:path');

// ---------- minimal PNG encoder ----------
function crc32(buf) {
  let c = ~0;
  for (let n = 0; n < buf.length; n++) {
    c ^= buf[n];
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 >>> 0);
  }
  return ~c >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}
function encodePng(width, height, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0;
    rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  return Buffer.concat([
    sig,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------- tiny SVG rasterizer (paths -> coverage, no AA beyond 2x2) ----------
// Supports: M/L (absolute), fill-rule evenodd, linearGradient vertical.
// Source shapes are authored with straight lines only.
function drawSvgPath(ctx, d, fill, gradient) {
  const { size, coverage } = ctx;
  const cmds = d.match(/[MLZmlz][^MLZmlz]*/g) || [];
  let cx = 0, cy = 0;
  let sx = 0, sy = 0; // subpath start (for Z close)
  const edges = [];
  for (const c of cmds) {
    const nums = (c.slice(1).match(/-?\d*\.?\d+(?:e-?\d+)?/g) || []).map(Number);
    if (c[0] === 'M' || c[0] === 'm') {
      cx = c[0] === 'M' ? nums[0] : cx + nums[0];
      cy = c[0] === 'M' ? nums[1] : cy + nums[1];
      sx = cx; sy = cy;
    } else if (c[0] === 'L' || c[0] === 'l') {
      for (let i = 0; i + 1 < nums.length; i += 2) {
        const nx = c[0] === 'L' ? nums[i] : cx + nums[i];
        const ny = c[0] === 'L' ? nums[i + 1] : cy + nums[i + 1];
        edges.push([cy, cx, ny, nx]);
        cx = nx; cy = ny;
      }
    } else if (c[0] === 'Z' || c[0] === 'z') {
      if (cx !== sx || cy !== sy) edges.push([cy, cx, sy, sx]);
      cx = sx; cy = sy;
    }
  }
  const scale = ctx.ss;
  const S = size * scale;
  // Precise scanline fill: count crossings of pixel centers (x+0.5) per row.
  // Edges at exactly the crossing height are skipped (consistent convention).
  for (let y = 0; y < S; y++) {
    const yc = y + 0.5;
    const xs = [];
    for (const [y1, x1, y2, x2] of edges) {
      const ay = y1 * scale, ax = x1 * scale, by = y2 * scale, bx = x2 * scale;
      if (ay === by) continue;
      if ((yc < Math.min(ay, by)) || (yc >= Math.max(ay, by))) continue;
      const t = (yc - ay) / (by - ay);
      xs.push(ax + t * (bx - ax));
    }
    if (!xs.length) continue;
    xs.sort((p, q) => p - q);
    for (let k = 0; k + 1 < xs.length; k += 2) {
      const x0 = Math.max(0, Math.ceil(xs[k] - 0.5));
      const x1 = Math.min(S - 1, Math.floor(xs[k + 1] - 0.5));
      for (let x = x0; x <= x1; x++) ctx.coverage[y][x] = 1;
    }
  }
  // fill pass with 2x supersampling blend
  for (let y = 0; y < S; y++) {
    for (let x = 0; x < S; x++) {
      if (!coverage[y][x]) continue;
      const sx = Math.floor(x / scale), sy = Math.floor(y / scale);
      const pi = (sy * size + sx) * 4;
      let r, g, b;
      if (gradient) {
        const t = Math.min(1, Math.max(0, sy / size));
        r = gradient[0] + (gradient[3] - gradient[0]) * t;
        g = gradient[1] + (gradient[4] - gradient[1]) * t;
        b = gradient[2] + (gradient[5] - gradient[2]) * t;
      } else {
        r = fill[0]; g = fill[1]; b = fill[2];
      }
      const a = fill[3] / 255;
      const dstA = ctx.rgba[pi + 3] / 255;
      const outA = a + dstA * (1 - a);
      if (outA > 0) {
        ctx.rgba[pi] = (r * a + ctx.rgba[pi] * dstA * (1 - a)) / outA;
        ctx.rgba[pi + 1] = (g * a + ctx.rgba[pi + 1] * dstA * (1 - a)) / outA;
        ctx.rgba[pi + 2] = (b * a + ctx.rgba[pi + 2] * dstA * (1 - a)) / outA;
        ctx.rgba[pi + 3] = outA * 255;
      }
    }
  }
  // clear coverage
  for (let y = 0; y < S; y++) ctx.coverage[y].fill(0);
}

// Rounded-rect path approximated with line segments (rasterizer is
// straight-lines-only; 8 segments per corner is smooth at 512px/2x SS).
function roundedRectPath(x, y, w, h, r, seg = 8) {
  const pts = [];
  const corner = (cx, cy, a0, a1) => {
    for (let i = 0; i <= seg; i++) {
      const a = a0 + ((a1 - a0) * i) / seg;
      pts.push([cx + r * Math.cos(a), cy + r * Math.sin(a)]);
    }
  };
  corner(x + r, y + r, Math.PI, 1.5 * Math.PI);
  corner(x + w - r, y + r, 1.5 * Math.PI, 2 * Math.PI);
  corner(x + w - r, y + h - r, 2 * Math.PI, 2.5 * Math.PI);
  corner(x + r, y + h - r, 2.5 * Math.PI, 3 * Math.PI);
  return (
    'M' +
    pts.map((p) => `${Math.round(p[0] * 100) / 100} ${Math.round(p[1] * 100) / 100}`).join(' L') +
    ' Z'
  );
}

function renderIcon(size, opts = {}) {
  const ss = 2;
  const S = size * ss;
  const ctx = { size, ss, rgba: Buffer.alloc(size * size * 4), coverage: [] };
  for (let y = 0; y < S; y++) ctx.coverage.push(new Uint8Array(S));

  const u = size / 512;
  // Tray variant: nearly full-bleed square so the icon stays readable at
  // 16-20 px in the notification area.
  const radius = opts.square ? size * 0.1 : size * 0.225;
  // Background gradient plate: indigo -> violet, vertical.
  const grad = [0x6a, 0x5a, 0xff, 0x9b, 0x4d, 0xff];
  drawSvgPath(ctx, roundedRectPath(0, 0, size, size, radius), [0x6a, 0x5a, 0xff, 255], grad);

  if (!opts.square) {
    // Top gloss: translucent white band with matching top corners.
    drawSvgPath(ctx, roundedRectPath(0, 0, size, size * 0.46, radius), [255, 255, 255, 26], null);
  }

  // Shield with a soft drop shadow.
  const shield = (dy) =>
    [
      `M${256 * u} ${(100 + dy) * u}`,
      `L${416 * u} ${(156 + dy) * u}`,
      `L${416 * u} ${(286 + dy) * u}`,
      `L${256 * u} ${(444 + dy) * u}`,
      `L${96 * u} ${(286 + dy) * u}`,
      `L${96 * u} ${(156 + dy) * u}`,
      'Z',
    ].join(' ');
  if (!opts.square) drawSvgPath(ctx, shield(size * 0.022), [18, 10, 52, 80], null);
  drawSvgPath(ctx, shield(0), [255, 255, 255, 255], null);

  // Play triangle cutout: filled with the same vertical gradient as the
  // background, so it reads as a hole in the shield (shield = protection,
  // play = video). Shifted right for optical centering.
  const play = [
    `M${232 * u} ${204 * u}`,
    `L${232 * u} ${340 * u}`,
    `L${344 * u} ${272 * u}`,
    'Z',
  ].join(' ');
  drawSvgPath(ctx, play, [0x6a, 0x5a, 0xff, 255], grad);

  return encodePng(size, size, ctx.rgba);
}

// ---------- ICO container ----------
function makeIco(pngBuffers) {
  const count = pngBuffers.length;
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // type icon
  header.writeUInt16LE(count, 4);
  const entries = [];
  let offset = 6 + 16 * count;
  for (const { size, png } of pngBuffers) {
    const e = Buffer.alloc(16);
    e[0] = size >= 256 ? 0 : size;
    e[1] = size >= 256 ? 0 : size;
    e[2] = 0; e[3] = 0;
    e.writeUInt16LE(1, 4);
    e.writeUInt16LE(32, 6);
    e.writeUInt32LE(png.length, 8);
    e.writeUInt32LE(offset, 12);
    entries.push(e);
    offset += png.length;
  }
  return Buffer.concat([header, ...entries, ...pngBuffers.map((p) => p.png)]);
}

// ---------- output ----------
const outDir = path.join(__dirname, '..', 'src', 'assets');
fs.mkdirSync(outDir, { recursive: true });

const icon512 = renderIcon(512);
fs.writeFileSync(path.join(outDir, 'icon.png'), icon512);

const sizes = [16, 24, 32, 48, 64, 128, 256];
const ico = makeIco(sizes.map((s) => ({ size: s, png: renderIcon(s) })));
fs.writeFileSync(path.join(outDir, 'icon.ico'), ico);

// Tray icons are embedded into the app as base64 data URLs (src/main/tray-icons.js):
// no file-path loading at runtime, so the tray icon can never come up blank.
const trayModule =
  "'use strict';\n" +
  "// Auto-generated by test/generate-icons.js — do not edit by hand.\n" +
  `module.exports = {\n` +
  `  tray16: 'data:image/png;base64,${renderIcon(16, { square: true }).toString('base64')}',\n` +
  `  tray32: 'data:image/png;base64,${renderIcon(32, { square: true }).toString('base64')}',\n` +
  `};\n`;
fs.writeFileSync(path.join(__dirname, '..', 'src', 'main', 'tray-icons.js'), trayModule);

console.log('icons written to', outDir, '+ src/main/tray-icons.js');
