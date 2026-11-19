#!/usr/bin/env node
/**
 * check-style-contrast.mjs — WCAG contrast gate for the SimpleFinance stylesheet.
 *
 * Purpose
 *   Reads the BUILT frontend CSS (what the browser really gets), resolves the
 *   CSS custom properties per color mode (light = `:root` / `[data-bs-theme=light]`,
 *   dark = light overlaid with `[data-bs-theme=dark]`) and measures WCAG 2.x
 *   contrast ratios for the financial colors, body text, Bootstrap emphasis
 *   text and the chart palette against the authored surfaces. It also checks
 *   that Bootstrap's --bs-body-bg and the authored --sf-surface-body agree.
 *
 * Usage
 *   node scripts/check-style-contrast.mjs [--css-dir DIR] [--json]
 *
 *   --css-dir  default <repo>/frontend/dist/assets (run the frontend build first)
 *   --json     print { ok, modes: { light: [...], dark: [...] } } instead of lines
 *
 *   The repo root is resolved from this script's own location, so the current
 *   working directory does not matter.
 *
 * Exit codes
 *   0  every check passes in both modes
 *   1  at least one contrast or consistency check fails
 *   2  setup error: no CSS, a required token missing or unparseable in a mode,
 *      a translucent background, or any unexpected error. Never a silent pass.
 *
 * Checks (per mode)
 *   TEXT      min 4.5  financial, chart-text, body and muted text on body/card/raised
 *   EMPHASIS  min 4.5  Bootstrap *-text-emphasis colors on card/raised
 *   GRAPHICS  min 3.0  chart series colors on card
 *   CONSIST.  equal    --bs-body-bg vs --sf-surface-body
 *
 * Dependencies: Node built-ins only.
 */

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const MAX_VAR_DEPTH = 5

const TEXT_FG = [
  '--sf-income',
  '--sf-expense',
  '--sf-transfer',
  '--sf-neutral',
  '--sf-chart-text',
  '--bs-body-color',
  '--bs-secondary-color',
]
const TEXT_BG = ['--sf-surface-body', '--sf-surface-card', '--sf-surface-raised']
const EMPHASIS_FG = [
  '--bs-primary-text-emphasis',
  '--bs-secondary-text-emphasis',
  '--bs-success-text-emphasis',
  '--bs-danger-text-emphasis',
  '--bs-warning-text-emphasis',
  '--bs-info-text-emphasis',
]
const EMPHASIS_BG = ['--sf-surface-card', '--sf-surface-raised']
const GRAPHICS_FG = [
  '--sf-chart-primary',
  '--sf-chart-gray',
  '--sf-chart-1',
  '--sf-chart-2',
  '--sf-chart-3',
  '--sf-chart-4',
  '--sf-chart-5',
  '--sf-chart-6',
  '--sf-income',
  '--sf-expense',
]
const GRAPHICS_BG = ['--sf-surface-card']

/** The full check list, in report order. */
export const CHECKS = [
  ...TEXT_FG.flatMap((fg) => TEXT_BG.map((bg) => ({ kind: 'text', fg, bg, min: 4.5 }))),
  ...EMPHASIS_FG.flatMap((fg) => EMPHASIS_BG.map((bg) => ({ kind: 'emphasis', fg, bg, min: 4.5 }))),
  ...GRAPHICS_FG.flatMap((fg) => GRAPHICS_BG.map((bg) => ({ kind: 'graphics', fg, bg, min: 3 }))),
]
const CONSISTENCY = { kind: 'consistency', a: '--bs-body-bg', b: '--sf-surface-body' }

class SetupError extends Error {}

// ---------------------------------------------------------------------------
// Color math
// ---------------------------------------------------------------------------

function parseNumber(text, scale) {
  const t = text.trim()
  if (t === '') return NaN
  if (t.endsWith('%')) return (Number(t.slice(0, -1)) / 100) * scale
  return Number(t)
}

/**
 * Parses #rgb, #rgba, #rrggbb, #rrggbbaa and rgb()/rgba() (any letter case,
 * comma or space syntax, optional `/ alpha`). Returns { r, g, b, a } or null.
 */
export function parseColor(input) {
  if (typeof input !== 'string') return null
  const text = input.trim()

  const hex = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(text)
  if (hex) {
    let h = hex[1]
    if (h.length <= 4) h = [...h].map((c) => c + c).join('')
    const r = parseInt(h.slice(0, 2), 16)
    const g = parseInt(h.slice(2, 4), 16)
    const b = parseInt(h.slice(4, 6), 16)
    const a = h.length === 8 ? parseInt(h.slice(6, 8), 16) / 255 : 1
    return { r, g, b, a }
  }

  const fn = /^rgba?\((.*)\)$/i.exec(text)
  if (!fn) return null
  const inner = fn[1].trim()
  let parts
  let alphaText
  if (inner.includes(',')) {
    parts = inner.split(',')
    if (parts.length === 4) alphaText = parts.pop()
    if (parts.length !== 3) return null
  } else {
    const [channels, alpha, extra] = inner.split('/')
    if (extra !== undefined) return null
    parts = channels.trim().split(/\s+/)
    if (parts.length !== 3) return null
    alphaText = alpha
  }
  const [r, g, b] = parts.map((p) => parseNumber(p, 255))
  const a = alphaText === undefined ? 1 : parseNumber(alphaText, 1)
  const channelsOk = [r, g, b].every((c) => Number.isFinite(c) && c >= 0 && c <= 255)
  if (!channelsOk || !Number.isFinite(a) || a < 0 || a > 1) return null
  return { r, g, b, a }
}

function toColor(value) {
  const c = typeof value === 'string' ? parseColor(value) : value
  if (!c) throw new SetupError(`unparseable color: ${value}`)
  return c
}

/** Alpha-composites fg over an opaque bg. */
function composite(fg, bg) {
  if (fg.a >= 1) return fg
  return {
    r: fg.r * fg.a + bg.r * (1 - fg.a),
    g: fg.g * fg.a + bg.g * (1 - fg.a),
    b: fg.b * fg.a + bg.b * (1 - fg.a),
    a: 1,
  }
}

function channel(c) {
  const s = c / 255
  return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
}

function luminance({ r, g, b }) {
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
}

/**
 * WCAG 2.x contrast ratio. Accepts color strings or { r, g, b, a } objects.
 * A translucent foreground is composited over the background first; a
 * translucent background throws (its real color depends on what is beneath).
 */
export function contrastRatio(fgInput, bgInput) {
  const bg = toColor(bgInput)
  if (bg.a < 1) throw new SetupError(`background is translucent: ${bgInput}`)
  const fg = composite(toColor(fgInput), bg)
  const l1 = luminance(fg)
  const l2 = luminance(bg)
  const [hi, lo] = l1 >= l2 ? [l1, l2] : [l2, l1]
  return (hi + 0.05) / (lo + 0.05)
}

// ---------------------------------------------------------------------------
// CSS reading
// ---------------------------------------------------------------------------

const LIGHT_ITEM = /^(?::root|\[data-bs-theme=(?:light|"light"|'light')\])$/
const DARK_ITEM = /^\[data-bs-theme=(?:dark|"dark"|'dark')\]$/
const RULE = /([^{}]+)\{([^{}]*)\}/g
const DECL = /(--[\w-]+)\s*:\s*((?:[^;"']|"[^"]*"|'[^']*')*)/g

/** Builds the light and dark custom-property maps from concatenated CSS. */
export function collectModes(cssText) {
  const text = cssText.replace(/\/\*[\s\S]*?\*\//g, '')
  const light = new Map()
  const darkOnly = new Map()
  for (const [, rawSelector, body] of text.matchAll(RULE)) {
    // Drop at-rule statements (@charset/@import) that precede the selector.
    const selector = rawSelector.slice(rawSelector.lastIndexOf(';') + 1)
    const items = selector.split(',').map((s) => s.trim())
    const isLight = items.some((s) => LIGHT_ITEM.test(s))
    const isDark = items.some((s) => DARK_ITEM.test(s))
    if (!isLight && !isDark) continue
    for (const [, name, value] of body.matchAll(DECL)) {
      if (isLight) light.set(name.trim(), value.trim())
      if (isDark) darkOnly.set(name.trim(), value.trim())
    }
  }
  const dark = new Map([...light, ...darkOnly])
  return { light, dark }
}

/** Finds the matching close paren for the `(` at index `open`. */
function closeParen(text, open) {
  let depth = 0
  for (let i = open; i < text.length; i++) {
    if (text[i] === '(') depth++
    else if (text[i] === ')' && --depth === 0) return i
  }
  return -1
}

/** Substitutes every var(--x[, fallback]) using `map`, up to MAX_VAR_DEPTH levels. */
export function resolveValue(value, map, depth = 0) {
  if (value === undefined) return undefined
  if (!/var\(/i.test(value)) return value
  if (depth >= MAX_VAR_DEPTH) throw new SetupError(`var() nesting deeper than ${MAX_VAR_DEPTH}: ${value}`)
  let out = ''
  let i = 0
  const lower = value.toLowerCase()
  while (i < value.length) {
    const at = lower.indexOf('var(', i)
    if (at === -1) {
      out += value.slice(i)
      break
    }
    out += value.slice(i, at)
    const open = at + 3
    const close = closeParen(value, open)
    if (close === -1) throw new SetupError(`unbalanced var(): ${value}`)
    const inner = value.slice(open + 1, close)
    let comma = -1
    let level = 0
    for (let k = 0; k < inner.length; k++) {
      if (inner[k] === '(') level++
      else if (inner[k] === ')') level--
      else if (inner[k] === ',' && level === 0) {
        comma = k
        break
      }
    }
    const name = (comma === -1 ? inner : inner.slice(0, comma)).trim()
    const fallback = comma === -1 ? undefined : inner.slice(comma + 1).trim()
    const target = map.has(name) ? map.get(name) : fallback
    if (target === undefined) return undefined
    const resolved = resolveValue(target, map, depth + 1)
    if (resolved === undefined) return undefined
    out += resolved
    i = close + 1
  }
  return resolveValue(out, map, depth + 1)
}

// ---------------------------------------------------------------------------
// Evaluation
// ---------------------------------------------------------------------------

function lookup(map, mode, name, errors, role) {
  if (!map.has(name)) {
    errors.push(`${name} is not defined in ${mode} mode`)
    return null
  }
  let resolved
  try {
    resolved = resolveValue(map.get(name), map)
  } catch (err) {
    errors.push(`${name} in ${mode} mode: ${err.message}`)
    return null
  }
  if (resolved === undefined) {
    errors.push(`${name} in ${mode} mode references an undefined variable: ${map.get(name)}`)
    return null
  }
  const color = parseColor(resolved)
  if (!color) {
    errors.push(`${name} in ${mode} mode is not a parseable color: ${resolved}`)
    return null
  }
  if (role === 'bg' && color.a < 1) {
    errors.push(`${name} in ${mode} mode is a translucent background: ${resolved}`)
    return null
  }
  return color
}

function hexOf({ r, g, b }) {
  return '#' + [r, g, b].map((c) => Math.round(c).toString(16).padStart(2, '0')).join('')
}

/** Runs every check for one mode. Returns { results, errors }. */
export function evaluateMode(mode, map) {
  const errors = []
  const results = []
  const colors = new Map()
  const get = (name, role) => {
    const key = `${role}:${name}`
    if (!colors.has(key)) colors.set(key, lookup(map, mode, name, errors, role))
    return colors.get(key)
  }
  for (const check of CHECKS) {
    const fg = get(check.fg, 'fg')
    const bg = get(check.bg, 'bg')
    if (!fg || !bg) continue
    const ratio = contrastRatio(fg, bg)
    results.push({ ...check, ratio: Math.floor(ratio * 100) / 100, pass: ratio >= check.min })
  }
  const a = get(CONSISTENCY.a, 'bg')
  const b = get(CONSISTENCY.b, 'bg')
  if (a && b) {
    const same = hexOf(a) === hexOf(b)
    results.push({ ...CONSISTENCY, valueA: hexOf(a), valueB: hexOf(b), pass: same })
  }
  return { results, errors: [...new Set(errors)] }
}

function formatResult(mode, r) {
  const verdict = r.pass ? 'PASS' : 'FAIL'
  if (r.kind === 'consistency') return `${mode}  ${r.a} = ${r.b}  ${r.valueA} vs ${r.valueB}  ${verdict}`
  return `${mode}  ${r.fg} on ${r.bg}  ${r.ratio.toFixed(2)}:1 (min ${r.min})  ${verdict}`
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const opts = { json: false, cssDir: join(repoRoot, 'frontend', 'dist', 'assets') }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--json') opts.json = true
    else if (arg === '--css-dir') {
      const value = argv[++i]
      if (!value) throw new SetupError('--css-dir needs a directory')
      opts.cssDir = resolve(value)
    } else throw new SetupError(`unknown argument: ${arg}`)
  }
  return opts
}

function readCss(dir) {
  const files =
    existsSync(dir) && statSync(dir).isDirectory()
      ? readdirSync(dir)
          .filter((f) => f.toLowerCase().endsWith('.css'))
          .sort()
      : []
  if (files.length === 0) {
    throw new SetupError(`no CSS in ${dir} — run npm --prefix frontend run build first`)
  }
  return files.map((f) => readFileSync(join(dir, f), 'utf8')).join('\n')
}

export function main(argv = process.argv.slice(2)) {
  try {
    const opts = parseArgs(argv)
    const modes = collectModes(readCss(opts.cssDir))
    const report = {}
    const errors = []
    for (const mode of ['light', 'dark']) {
      const { results, errors: modeErrors } = evaluateMode(mode, modes[mode])
      report[mode] = results
      errors.push(...modeErrors)
    }
    if (errors.length) {
      for (const e of errors) process.stderr.write(`check-style-contrast: ${e}\n`)
      process.exitCode = 2
      return
    }
    const ok = [...report.light, ...report.dark].every((r) => r.pass)
    if (opts.json) {
      process.stdout.write(JSON.stringify({ ok, modes: report }, null, 2) + '\n')
    } else {
      const lines = [
        ...report.light.map((r) => formatResult('light', r)),
        ...report.dark.map((r) => formatResult('dark', r)),
        `Result: ${ok ? 'PASS' : 'FAIL'}`,
      ]
      process.stdout.write(lines.join('\n') + '\n')
    }
    process.exitCode = ok ? 0 : 1
  } catch (err) {
    process.stderr.write(`check-style-contrast: ${err instanceof Error ? err.message : String(err)}\n`)
    process.exitCode = 2
  }
}

function isEntry() {
  const self = resolve(fileURLToPath(import.meta.url))
  const invoked = resolve(process.argv[1] ?? '')
  return process.platform === 'win32' ? self.toLowerCase() === invoked.toLowerCase() : self === invoked
}

if (isEntry()) main()
