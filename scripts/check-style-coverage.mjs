#!/usr/bin/env node
/**
 * check-style-coverage.mjs — class-coverage gate for the SimpleFinance frontend.
 *
 * Purpose
 *   STYLE-03 gate: every class our screens use must exist in the built CSS.
 *   It collects the static class tokens used by our own screen code (features/,
 *   views/app/, the app shell in shell/ and the shared components those screens
 *   render) and checks that each one appears as a selector in the built
 *   stylesheet. A token may instead sit on ALLOWLIST below, always with a reason.
 *
 * Usage
 *   node scripts/check-style-coverage.mjs [--css-dir DIR] [--src-dir DIR] [--json]
 *
 *   --css-dir  default <repo>/frontend/dist/assets (every *.css file is read)
 *   --src-dir  default <repo>/frontend/src
 *   --json     print one JSON object on stdout instead of the human report
 *
 *   The repo root is resolved from this script's own location, so the current
 *   working directory does not matter. Build first:
 *     npm --prefix frontend run build
 *
 * Exit codes
 *   0  every static token is covered or allowlisted
 *   1  at least one static token is missing from the built CSS
 *   2  setup error: no CSS in the css dir, a scope entry missing, a bad argument
 *      or any unexpected error
 *
 * Extraction (per .ts/.tsx file in SCOPE)
 *   a) className="..." / className='...' and the same for every prop whose name
 *      ends in ClassName (inputClassName, labelClassName, dialogClassName, ...).
 *   b) className={...}: the expression is scanned to its matching brace; every
 *      quoted string literal inside is taken, plus the static text of every
 *      template literal (each ${...} segment is replaced by a "$" marker; quoted
 *      strings inside the segment are taken as well).
 *   c) anywhere in the file, a quoted string literal whose whole content matches
 *      ^(text|bg|border|fs|avatar)-[a-z0-9-]+$ (class names held in a Record).
 *   Values are split on whitespace. A token that still contains $, { or } or
 *   ends with "-" is DYNAMIC: listed for manual review, never a failure.
 */

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, extname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

/** Files and folders (relative to the src dir) whose classes must be styled. */
const SCOPE = [
  'features',
  'views/app',
  'shell',
  'components/charts',
  'components/MoneyText.tsx',
  'components/StatTile.tsx',
  'components/PageHeader.tsx',
]

const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx'])

/** token -> reason. Exact names only, no wildcards. */
const ALLOWLIST = {
  'react-select':
    'react-select host element; its parts are styled through .react-select__* selectors in src/styles/_vendor.scss',
}

const PROP_PATTERN = /\b(className|[A-Za-z_$][\w$]*ClassName)\s*=(?![=>])\s*/g
const RECORD_PATTERN = /(['"])((?:text|bg|border|fs|avatar)-[a-z0-9-]+)\1/g
const VALID_TOKEN = /^[a-z][a-z0-9_-]*$/i
const DYNAMIC_MARK = '$'

class UsageError extends Error {}
class SetupError extends Error {}

function parseArgs(argv) {
  const opts = {
    json: false,
    cssDir: join(repoRoot, 'frontend', 'dist', 'assets'),
    srcDir: join(repoRoot, 'frontend', 'src'),
  }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--json') {
      opts.json = true
    } else if (arg === '--css-dir' || arg === '--src-dir') {
      const value = argv[++i]
      if (value === undefined || value.startsWith('--')) {
        throw new UsageError(`${arg} requires a directory argument`)
      }
      if (arg === '--css-dir') opts.cssDir = resolve(value)
      else opts.srcDir = resolve(value)
    } else {
      throw new UsageError(`unknown argument: ${arg}`)
    }
  }
  return opts
}

function toPosix(p) {
  return p.split(sep).join('/')
}

function byCodeUnit(a, b) {
  return a < b ? -1 : a > b ? 1 : 0
}

function statOrNull(p) {
  try {
    return statSync(p)
  } catch {
    return null
  }
}

/** Recursively collect .ts/.tsx files (absolute paths), symbolic links skipped. */
function walkSources(root) {
  const out = []
  const visit = (dir) => {
    const entries = readdirSync(dir, { withFileTypes: true })
    entries.sort((a, b) => byCodeUnit(a.name, b.name))
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) visit(full)
      else if (entry.isFile() && SOURCE_EXTENSIONS.has(extname(entry.name))) out.push(full)
    }
  }
  visit(root)
  return out
}

function collectScopeFiles(srcDir) {
  const files = new Set()
  for (const entry of SCOPE) {
    const full = join(srcDir, ...entry.split('/'))
    const st = statOrNull(full)
    if (!st) throw new SetupError(`scope entry ${entry} not found under ${srcDir}`)
    if (st.isDirectory()) for (const f of walkSources(full)) files.add(f)
    else if (st.isFile()) files.add(full)
  }
  return [...files].sort(byCodeUnit)
}

/** Read a '...' or "..." string starting at i (the quote). Returns { value, end } (end = index after the closing quote). */
function readQuoted(src, i) {
  const quote = src[i]
  let value = ''
  let j = i + 1
  while (j < src.length && src[j] !== quote) {
    if (src[j] === '\\' && j + 1 < src.length) {
      value += src[j + 1]
      j += 2
      continue
    }
    if (src[j] === '\n') break
    value += src[j]
    j++
  }
  return { value, end: j + 1 }
}

/**
 * Read a template literal starting at i (the backtick). Static text is pushed
 * with a DYNAMIC_MARK in place of each ${...} segment; strings inside segments
 * are pushed by scanExpression. Returns the index after the closing backtick.
 */
function readTemplate(src, i, out) {
  let text = ''
  let j = i + 1
  while (j < src.length && src[j] !== '`') {
    if (src[j] === '\\' && j + 1 < src.length) {
      text += src[j + 1]
      j += 2
      continue
    }
    if (src[j] === '$' && src[j + 1] === '{') {
      text += DYNAMIC_MARK
      j = scanExpression(src, j + 2, out) + 1
      continue
    }
    text += src[j]
    j++
  }
  out.push(text)
  return j + 1
}

/**
 * Scan code starting at i until the brace that closes the current level.
 * Pushes every string literal found to `out`. Returns the index of that brace
 * (or src.length when unbalanced).
 */
function scanExpression(src, i, out) {
  let depth = 0
  let j = i
  while (j < src.length) {
    const ch = src[j]
    if (ch === '"' || ch === "'") {
      const { value, end } = readQuoted(src, j)
      out.push(value)
      j = end
    } else if (ch === '`') {
      j = readTemplate(src, j, out)
    } else if (ch === '/' && src[j + 1] === '/') {
      const nl = src.indexOf('\n', j)
      j = nl === -1 ? src.length : nl + 1
    } else if (ch === '/' && src[j + 1] === '*') {
      const close = src.indexOf('*/', j + 2)
      j = close === -1 ? src.length : close + 2
    } else if (ch === '{') {
      depth++
      j++
    } else if (ch === '}') {
      if (depth === 0) return j
      depth--
      j++
    } else {
      j++
    }
  }
  return src.length
}

/** Every raw class value (a string that may hold several tokens) in one source file. */
function extractValues(src) {
  const values = []
  PROP_PATTERN.lastIndex = 0
  let m
  while ((m = PROP_PATTERN.exec(src)) !== null) {
    const at = m.index + m[0].length
    const ch = src[at]
    if (ch === '"' || ch === "'") {
      values.push(readQuoted(src, at).value)
    } else if (ch === '{') {
      scanExpression(src, at + 1, values)
    }
  }
  RECORD_PATTERN.lastIndex = 0
  while ((m = RECORD_PATTERN.exec(src)) !== null) values.push(m[2])
  return values
}

function trimPunctuation(token) {
  return token.replace(/^[^A-Za-z0-9_\-${}]+/, '').replace(/[^A-Za-z0-9_\-${}]+$/, '')
}

/** Split raw values into { statics, dynamics } token sets. */
function classifyTokens(values) {
  const statics = new Set()
  const dynamics = new Set()
  for (const value of values) {
    for (const raw of value.split(/\s+/)) {
      const token = trimPunctuation(raw)
      if (!token || token === DYNAMIC_MARK) continue
      if (/[${}]/.test(token) || token.endsWith('-')) {
        dynamics.add(token)
        continue
      }
      if (!VALID_TOKEN.test(token)) continue
      statics.add(token)
    }
  }
  return { statics, dynamics }
}

function readCss(cssDir) {
  const st = statOrNull(cssDir)
  const names = st && st.isDirectory() ? readdirSync(cssDir).filter((n) => n.endsWith('.css')).sort(byCodeUnit) : []
  if (names.length === 0) {
    throw new SetupError(`no CSS in ${cssDir} — run npm --prefix frontend run build first`)
  }
  return names.map((n) => readFileSync(join(cssDir, n), 'utf8')).join('\n')
}

function escapeRegExp(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function isCovered(css, token) {
  return new RegExp(`\\.${escapeRegExp(token)}(?![A-Za-z0-9_-])`).test(css)
}

function scan(opts) {
  const files = collectScopeFiles(opts.srcDir)
  const css = readCss(opts.cssDir)

  const staticFiles = new Map()
  const dynamicFiles = new Map()
  const note = (map, token, file) => {
    if (!map.has(token)) map.set(token, new Set())
    map.get(token).add(file)
  }

  for (const file of files) {
    const rel = toPosix(relative(opts.srcDir, file))
    const { statics, dynamics } = classifyTokens(extractValues(readFileSync(file, 'utf8')))
    for (const t of statics) note(staticFiles, t, rel)
    for (const t of dynamics) note(dynamicFiles, t, rel)
  }

  const tokens = [...staticFiles.keys()].sort(byCodeUnit)
  const covered = []
  const allowlisted = []
  const missing = []
  for (const token of tokens) {
    if (isCovered(css, token)) covered.push(token)
    else if (Object.hasOwn(ALLOWLIST, token)) allowlisted.push(token)
    else missing.push({ token, files: [...staticFiles.get(token)].sort(byCodeUnit) })
  }
  const dynamic = [...dynamicFiles.keys()]
    .sort(byCodeUnit)
    .map((token) => ({ token, files: [...dynamicFiles.get(token)].sort(byCodeUnit) }))

  return { ok: missing.length === 0, files: files.length, tokens, covered, allowlisted, dynamic, missing }
}

function printHuman(r) {
  const lines = []
  lines.push('check-style-coverage — class census vs built CSS')
  lines.push(`Scope: ${r.files} files, ${r.tokens.length} distinct class tokens`)
  lines.push(`Covered: ${r.covered.length}`)
  lines.push(`Allowlisted: ${r.allowlisted.length}`)
  for (const t of r.allowlisted) lines.push(`  ${t}  (${ALLOWLIST[t]})`)
  lines.push(`Dynamic (manual review): ${r.dynamic.length}`)
  for (const d of r.dynamic) lines.push(`  ${d.token}  ${d.files.join(', ')}`)
  lines.push(`Missing: ${r.missing.length}`)
  for (const m of r.missing) lines.push(`  ${m.token}  ${m.files.join(', ')}`)
  lines.push(`Result: ${r.ok ? 'PASS' : 'FAIL'}`)
  process.stdout.write(lines.join('\n') + '\n')
}

function main() {
  const opts = parseArgs(process.argv.slice(2))
  const result = scan(opts)
  if (opts.json) process.stdout.write(JSON.stringify(result, null, 2) + '\n')
  else printHuman(result)
  process.exitCode = result.ok ? 0 : 1
}

try {
  main()
} catch (err) {
  process.stderr.write(`check-style-coverage: ${err && err.message ? err.message : String(err)}\n`)
  process.exitCode = 2
}
