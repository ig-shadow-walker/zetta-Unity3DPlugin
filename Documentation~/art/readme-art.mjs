/**
 * Generates the README artwork in Documentation~/images/ from the Zetta 3D brand tokens:
 *   banner-dark.svg, banner-light.svg      the header
 *
 *   node "Documentation~/art/readme-art.mjs"
 *
 * Same system as the Zetta 3D CLI's README header. Fonts (Figtree, Geist, Geist Mono; SIL Open Font
 * License) are embedded so the SVGs look the same on GitHub as in the brand kit. Blue is a fill, a dot
 * or text — never a stroke.
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = dirname(fileURLToPath(import.meta.url))
const OUT = join(HERE, '..', 'images')
const font = (file) => readFileSync(join(HERE, 'fonts', file)).toString('base64')
const FONTS = `
  @font-face { font-family: 'Figtree'; font-weight: 400 900; src: url(data:font/woff2;base64,${font('figtree-latin.woff2')}) format('woff2'); }
  @font-face { font-family: 'Geist'; font-weight: 400 700; src: url(data:font/woff2;base64,${font('geist-latin.woff2')}) format('woff2'); }
  @font-face { font-family: 'Geist Mono'; font-weight: 400 600; src: url(data:font/woff2;base64,${font('geist-mono-latin.woff2')}) format('woff2'); }`

const T = {
  ink: '#15130F',
  inkSoft: '#2A2620',
  umber: '#55514A',
  stone: '#C2BCAE',
  sand: '#DFDACE',
  linen: '#EFEBE2',
  cream: '#F7F4EE',
  paper: '#FAF8F4',
  signal: '#1677FF',
  signalDeep: '#0E58C0',
  signalLight: '#69B1FF',
  success: '#3C7A5B',
}

const DISPLAY = `'Figtree', 'Helvetica Neue', Arial, sans-serif`
const SANS = `'Geist', -apple-system, 'Segoe UI', Arial, sans-serif`
const MONO = `'Geist Mono', ui-monospace, 'SF Mono', Menlo, Consolas, monospace`

/** The three-plane Zetta mark, scaled into a box at (x, y). */
function mark(x, y, size, fill) {
  const s = size / 200
  return `<g transform="translate(${x} ${y}) scale(${s})" fill="${fill}">
    <path d="M 26 48 L 122 28 L 174 48 L 78 68 Z"/>
    <path d="M 26 100 L 122 80 L 174 100 L 78 120 Z"/>
    <path d="M 26 152 L 122 132 L 174 152 L 78 172 Z"/>
  </g>`
}

const dots = (id, color, opacity) =>
  `<pattern id="${id}" width="24" height="24" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.1" fill="${color}" opacity="${opacity}"/></pattern>`

const esc = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

// ── Banner ──────────────────────────────────────────────────────────────────

function banner(theme) {
  const dark = theme === 'dark'
  const bg = dark ? T.ink : T.cream
  const fg = dark ? T.paper : T.ink
  const muted = dark ? T.stone : T.umber
  const card = dark ? '#1E1B16' : T.paper
  const cardLine = dark ? '#34302A' : T.sand
  const well = dark ? '#262219' : T.linen
  const bubble = dark ? T.inkSoft : T.linen
  const wash = dark ? 'rgba(22,119,255,0.16)' : '#E7F1FF'
  const washText = dark ? T.signalLight : T.signalDeep
  const W = 1280
  const H = 420

  // The Agent window on the right: a message, the agent at work, and a card asking the user for a prefab.
  const x = 742
  const y = 48
  const w = 476
  const h = 324
  const step = (cy, done, text) => `
    <circle cx="${x + 30}" cy="${cy - 4}" r="4" fill="${done ? T.success : T.signal}"/>
    <text x="${x + 44}" y="${cy}" font-family="${SANS}" font-size="13.5" fill="${muted}">${esc(text)}</text>`
  const agent = `<g>
    <rect x="${x}" y="${y}" width="${w}" height="${h}" rx="14" fill="${card}" stroke="${cardLine}"/>
    <circle cx="${x + 24}" cy="${y + 24}" r="6" fill="#FF5F57"/>
    <circle cx="${x + 44}" cy="${y + 24}" r="6" fill="#FEBC2E"/>
    <circle cx="${x + 64}" cy="${y + 24}" r="6" fill="#28C840"/>
    <text x="${x + w / 2}" y="${y + 29}" text-anchor="middle" font-family="${SANS}" font-size="13" fill="${muted}">Zetta Agent</text>
    <line x1="${x}" y1="${y + 46}" x2="${x + w}" y2="${y + 46}" stroke="${cardLine}"/>

    <rect x="${x + w - 238}" y="${y + 62}" width="214" height="34" rx="12" fill="${bubble}"/>
    <text x="${x + w - 131}" y="${y + 84}" text-anchor="middle" font-family="${SANS}" font-size="14" fill="${fg}">Make the gun fire rockets</text>
    ${step(y + 124, true, 'Inspecting Player/Gun')}

    <rect x="${x + 20}" y="${y + 140}" width="${w - 40}" height="124" rx="12" fill="${well}"/>
    <rect x="${x + 34}" y="${y + 154}" width="122" height="22" rx="11" fill="${wash}"/>
    <text x="${x + 95}" y="${y + 169}" text-anchor="middle" font-family="${SANS}" font-weight="600" font-size="12" fill="${washText}">Needs your input</text>
    <text x="${x + 34}" y="${y + 200}" font-family="${SANS}" font-weight="600" font-size="14.5" fill="${fg}">Which prefab should the gun fire?</text>
    <rect x="${x + 34}" y="${y + 212}" width="300" height="34" rx="8" fill="${card}" stroke="${cardLine}"/>
    <rect x="${x + 46}" y="${y + 222}" width="14" height="14" rx="3" fill="${T.signalLight}"/>
    <text x="${x + 70}" y="${y + 234}" font-family="${MONO}" font-size="13" fill="${fg}">Rocket.prefab</text>
    <rect x="${x + w - 96}" y="${y + 212}" width="62" height="34" rx="17" fill="${fg}"/>
    <text x="${x + w - 65}" y="${y + 234}" text-anchor="middle" font-family="${SANS}" font-weight="600" font-size="14" fill="${bg}">OK</text>

    ${step(y + 292, false, 'Editing Gun.cs and wiring the Rocket prefab…')}
  </g>`

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="Zetta 3D for Unity: 3D assets and an AI agent inside the Unity Editor">
  <title>Zetta 3D for Unity</title>
  <defs>
    <style>${FONTS}</style>
    ${dots('dots', dark ? T.paper : T.ink, dark ? 0.07 : 0.08)}
    <radialGradient id="glow" cx="0.78" cy="0.45" r="0.55">
      <stop offset="0" stop-color="${T.signal}" stop-opacity="${dark ? 0.22 : 0.12}"/>
      <stop offset="1" stop-color="${T.signal}" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <rect width="${W}" height="${H}" rx="20" fill="${bg}"/>
  <rect width="${W}" height="${H}" rx="20" fill="url(#dots)"/>
  <rect width="${W}" height="${H}" rx="20" fill="url(#glow)"/>

  ${mark(64, 56, 54, fg)}
  <text x="128" y="94" font-family="${DISPLAY}" font-weight="700" font-size="30" letter-spacing="-0.6" fill="${fg}">Zetta 3D</text>
  <rect x="262" y="70" width="104" height="30" rx="8" fill="${T.signal}"/>
  <text x="314" y="91" text-anchor="middle" font-family="${MONO}" font-weight="600" font-size="16" fill="#FFFFFF">for Unity</text>

  <text x="64" y="196" font-family="${DISPLAY}" font-weight="800" font-size="52" letter-spacing="-1.8" fill="${fg}">3D assets and an AI agent,</text>
  <text x="64" y="256" font-family="${DISPLAY}" font-weight="800" font-size="52" letter-spacing="-1.8" fill="${fg}">inside the Unity Editor.</text>
  <text x="64" y="308" font-family="${SANS}" font-size="20" fill="${muted}">Generate, refine and import 3D models without leaving Unity,</text>
  <text x="64" y="338" font-family="${SANS}" font-size="20" fill="${muted}">and build your game with an agent that sees your scene.</text>

  ${agent}
</svg>
`
}

writeFileSync(join(OUT, 'banner-dark.svg'), banner('dark'))
writeFileSync(join(OUT, 'banner-light.svg'), banner('light'))
console.log('Wrote Documentation~/images: banner-dark.svg, banner-light.svg')
