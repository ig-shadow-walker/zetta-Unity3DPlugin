// Draws the Zetta 3D window's tab icons: Editor/UI/Icons/zetta-tab.png, and the same with an upload badge
// (zetta-tab-update.png, blue; zetta-tab-required.png, red) shown while a newer version is out.
//
//   node Documentation~/art/tab-icons.mjs            # writes the three icons
//   node Documentation~/art/tab-icons.mjs --preview  # also writes 256 px previews here, to look at
//
// THE COMPACT MARK ON ITS VOID CHIP, as the site's favicon draws it (public/favicon.svg): below 32 px the primary
// mark's plane gaps fall under a pixel, and a bare mark vanishes on one skin of the Editor or the other — the
// chip reads on both. Plain Node, no packages: shapes are sampled 8×8 per pixel and written as PNG with zlib.
import { writeFileSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

const HERE = dirname(fileURLToPath(import.meta.url))
const ICONS = join(HERE, '..', '..', 'Editor', 'UI', 'Icons')

const VOID = [0x15, 0x13, 0x0f]
const PAPER = [0xfa, 0xf8, 0xf4]
const SIGNAL = [0x16, 0x77, 0xff]
const ERROR = [0xe4, 0x75, 0x6b]

// ── Shapes, in the favicon's 200-unit space ────────────────────────────────

const chip = (x, y) => {
  // Rounded square, 0..200, corner radius 44.
  const r = 44
  const cx = Math.min(Math.max(x, r), 200 - r)
  const cy = Math.min(Math.max(y, r), 200 - r)
  return x >= 0 && x <= 200 && y >= 0 && y <= 200 && (x - cx) ** 2 + (y - cy) ** 2 <= r * r
}

const planes = [
  [[26, 44], [122, 29], [174, 44], [78, 59]],
  [[26, 100], [122, 85], [174, 100], [78, 115]],
  [[26, 156], [122, 141], [174, 156], [78, 171]],
].map((p) => p.map(([x, y]) => [100 + (x - 100) * 0.94, 100 + (y - 100) * 0.94]))

function inPolygon(x, y, poly) {
  let inside = false
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i]
    const [xj, yj] = poly[j]
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside
  }
  return inside
}

const mark = (x, y) => planes.some((p) => inPolygon(x, y, p))

// The badge: a disc at the bottom right with a dark ring, and an upload arrow — head, shaft, and the tray under it.
const BADGE = { cx: 142, cy: 142, r: 58, ring: 9 }
const disc = (x, y, r) => (x - BADGE.cx) ** 2 + (y - BADGE.cy) ** 2 <= r * r
const arrow = (x, y) => {
  const { cx, cy } = BADGE
  const head = inPolygon(x, y, [[cx, cy - 36], [cx - 27, cy - 7], [cx + 27, cy - 7]])
  const shaft = x >= cx - 8 && x <= cx + 8 && y >= cy - 9 && y <= cy + 14
  const tray = x >= cx - 26 && x <= cx + 26 && y >= cy + 22 && y <= cy + 32
  return head || shaft || tray
}

/** The colour at a point, top layer first; null where the icon is transparent. */
function paint(x, y, badge) {
  if (badge) {
    if (disc(x, y, BADGE.r - BADGE.ring)) return arrow(x, y) ? PAPER : badge
    if (disc(x, y, BADGE.r)) return VOID
  }
  if (!chip(x, y)) return null
  return mark(x, y) ? PAPER : VOID
}

// ── Raster and PNG ─────────────────────────────────────────────────────────

function render(size, badge, samples = 8) {
  const rgba = Buffer.alloc(size * size * 4)
  const unit = 200 / size
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let r = 0, g = 0, b = 0, a = 0
      for (let sy = 0; sy < samples; sy++) {
        for (let sx = 0; sx < samples; sx++) {
          const c = paint((px + (sx + 0.5) / samples) * unit, (py + (sy + 0.5) / samples) * unit, badge)
          if (!c) continue
          r += c[0]; g += c[1]; b += c[2]; a++
        }
      }
      const o = (py * size + px) * 4
      if (a) {
        rgba[o] = Math.round(r / a)
        rgba[o + 1] = Math.round(g / a)
        rgba[o + 2] = Math.round(b / a)
      }
      rgba[o + 3] = Math.round((a / (samples * samples)) * 255)
    }
  }
  return png(size, size, rgba)
}

const CRC = new Uint32Array(256).map((_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
const crc32 = (buf) => {
  let c = 0xffffffff
  for (const byte of buf) c = CRC[(c ^ byte) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}
const chunk = (type, data) => {
  const head = Buffer.alloc(8)
  head.writeUInt32BE(data.length, 0)
  head.write(type, 4, 'ascii')
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0)
  return Buffer.concat([head, data, crc])
}
function png(w, h, rgba) {
  const header = Buffer.alloc(13)
  header.writeUInt32BE(w, 0)
  header.writeUInt32BE(h, 4)
  header[8] = 8 // bit depth
  header[9] = 6 // RGBA
  const rows = Buffer.alloc((w * 4 + 1) * h)
  for (let y = 0; y < h; y++) rgba.copy(rows, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4)
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

// 32 px: a tab shows its icon at 16 points, which is 32 pixels on a Retina screen.
const icons = { 'zetta-tab.png': null, 'zetta-tab-update.png': SIGNAL, 'zetta-tab-required.png': ERROR }
mkdirSync(ICONS, { recursive: true })
for (const [name, badge] of Object.entries(icons)) writeFileSync(join(ICONS, name), render(32, badge))
if (process.argv.includes('--preview'))
  for (const [name, badge] of Object.entries(icons)) writeFileSync(join(HERE, `preview-${name}`), render(256, badge))
console.log(`Wrote ${Object.keys(icons).join(', ')} to Editor/UI/Icons.`)
