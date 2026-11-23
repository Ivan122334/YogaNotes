import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

const outDir = path.resolve("Assets");
fs.mkdirSync(outDir, { recursive: true });

const ICO_SIZES = [16, 24, 32, 48, 64, 128, 256];

function roundedRectContains(x, y, rx, ry, rw, rh, r) {
  if (x < rx || x > rx + rw || y < ry || y > ry + rh) return false;
  const cx = Math.max(rx + r, Math.min(x, rx + rw - r));
  const cy = Math.max(ry + r, Math.min(y, ry + rh - r));
  const dx = x - cx;
  const dy = y - cy;
  return dx * dx + dy * dy <= r * r;
}

function pointToSegmentDist(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const lenSq = dx * dx + dy * dy;
  const t = lenSq === 0 ? 0 : Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / lenSq));
  const nx = x1 + t * dx;
  const ny = y1 + t * dy;
  return Math.hypot(px - nx, py - ny);
}

function pointInPolygon(px, py, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const xi = poly[i][0], yi = poly[i][1];
    const xj = poly[j][0], yj = poly[j][1];
    const intersect = (yi > py) !== (yj > py) && px < ((xj - xi) * (py - yi)) / (yj - yi) + xi;
    if (intersect) inside = !inside;
  }
  return inside;
}

function polygonEdgeDist(px, py, poly) {
  let minD = Infinity;
  for (let i = 0; i < poly.length; i++) {
    const a = poly[i];
    const b = poly[(i + 1) % poly.length];
    minD = Math.min(minD, pointToSegmentDist(px, py, a[0], a[1], b[0], b[1]));
  }
  return minD;
}

const penPoly = [
  [388, 188],
  [432, 232],
  [296, 368],
  [240, 380],
  [252, 324]
];

const ruledLines = [
  [184, 160, 332, 160],
  [184, 212, 332, 212],
  [184, 264, 296, 264],
  [184, 316, 268, 316]
];

function sampleColor(u, v) {
  // Coordinates are in the 512x512 SVG space
  if (!roundedRectContains(u, v, 24, 24, 464, 464, 96)) {
    return [0, 0, 0, 0];
  }

  let color = [0x77, 0x19, 0xaa, 255];

  if (roundedRectContains(u, v, 116, 88, 248, 336, 24)) {
    color = u < 160 ? [0xe9, 0xd5, 0xff, 255] : [0xff, 0xff, 0xff, 255];

    for (const [x1, y1, x2, y2] of ruledLines) {
      if (pointToSegmentDist(u, v, x1, y1, x2, y2) <= 7) {
        color = [0xcb, 0xd5, 0xe1, 255];
        break;
      }
    }
  }

  if (pointInPolygon(u, v, penPoly)) {
    color = [0x1e, 0x29, 0x3b, 255];
  } else if (polygonEdgeDist(u, v, penPoly) <= 6) {
    color = [0xff, 0xff, 0xff, 255];
  }

  return color;
}

function renderRgba(size) {
  const buf = Buffer.alloc(size * size * 4);
  const samples = size <= 64 ? 4 : 2;
  const invTotal = 1 / (samples * samples);

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < samples; sy++) {
        for (let sx = 0; sx < samples; sx++) {
          const u = ((x + (sx + 0.5) / samples) / size) * 512;
          const v = ((y + (sy + 0.5) / samples) / size) * 512;
          const [sr, sg, sb, sa] = sampleColor(u, v);
          r += sr * sa;
          g += sg * sa;
          b += sb * sa;
          a += sa;
        }
      }
      const idx = (y * size + x) * 4;
      const avgA = a * invTotal;
      if (avgA > 0) {
        buf[idx] = Math.round(r / a);
        buf[idx + 1] = Math.round(g / a);
        buf[idx + 2] = Math.round(b / a);
        buf[idx + 3] = Math.round(avgA);
      }
    }
  }
  return buf;
}

const crcTable = new Uint32Array(256);
for (let n = 0; n < 256; n++) {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
  }
  crcTable[n] = c >>> 0;
}

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    c = crcTable[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const typeBuf = Buffer.from(type, "ascii");
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

function encodePng(size, rgba) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0; // None filter
    rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const compressed = zlib.deflateSync(raw, { level: 9 });
  return Buffer.concat([
    sig,
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", compressed),
    pngChunk("IEND", Buffer.alloc(0))
  ]);
}

function encodeIco(entries) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // ICO type
  header.writeUInt16LE(entries.length, 4);

  const dirEntries = [];
  let offset = 6 + entries.length * 16;

  for (const { size, png } of entries) {
    const dir = Buffer.alloc(16);
    dir[0] = size === 256 ? 0 : size;
    dir[1] = size === 256 ? 0 : size;
    dir[2] = 0;
    dir[3] = 0;
    dir.writeUInt16LE(1, 4);  // color planes
    dir.writeUInt16LE(32, 6); // bits per pixel
    dir.writeUInt32LE(png.length, 8);
    dir.writeUInt32LE(offset, 12);
    dirEntries.push(dir);
    offset += png.length;
  }

  return Buffer.concat([header, ...dirEntries, ...entries.map((e) => e.png)]);
}

const icoEntries = ICO_SIZES.map((size) => ({
  size,
  png: encodePng(size, renderRgba(size))
}));

fs.writeFileSync(path.join(outDir, "app-icon.ico"), encodeIco(icoEntries));
fs.writeFileSync(path.join(outDir, "app-icon-512.png"), encodePng(512, renderRgba(512)));
console.log("Generated Assets/app-icon.ico and Assets/app-icon-512.png");
