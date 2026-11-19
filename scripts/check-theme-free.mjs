#!/usr/bin/env node
/**
 * check-theme-free.mjs — license verifier for the SimpleFinance frontend.
 *
 * Purpose
 *   Proves that no file under frontend/ is copied from the paid admin-template
 *   StarterKit (TS and JS kits), so the repository can be published as open
 *   source. Three checks feed one report and one exit code:
 *
 *   1. Identity. sha256 of the raw bytes, so it is path-independent: a renamed
 *      or moved copy is still caught.
 *   2. Strings. Every case-insensitive match of STRING_PATTERN (the template's
 *      vendor and product names) left in frontend/ text files (binary files, detected by a NUL byte in the first
 *      8000 bytes, are skipped).
 *   3. Near-duplicates. A copy with a few edited lines has a different hash, so
 *      every frontend text file is also scored against every kit text file:
 *        - Normalization: block comments (slash-star and HTML), line comments
 *          that start a line or follow whitespace (so URLs survive), and lines
 *          starting with '#'. Comment markers inside quoted strings, and a block
 *          opener with no closer (a '/' + '*' glob in an ignore file), are kept
 *          as text, so they can never hide the rest of a file. Then whitespace runs are
 *          collapsed and these lines are dropped: empty, under 12 characters,
 *          starting with '#', import statements (also multi-line ones),
 *          re-exports (export ... from), and SCSS @use/@import/@forward.
 *        - Boilerplate: a line found in 10 or more distinct kit files is
 *          ubiquitous and is removed on both sides before scoring.
 *        - Units: for .json files, the ordered 5-line windows ("shingles") of
 *          the remaining lines, because JSON key/value lines are facts (option
 *          names, dependency pins) that cannot be reworded, while a copied
 *          manifest still shows up as long runs in the same order. For every
 *          other file, the set of remaining lines, which also catches a copy
 *          with scattered edits.
 *        - Score: shared units with the single closest kit file divided by the
 *          file's own units. A file with fewer than 8 units is too small to
 *          judge and is listed, not failed. A score at or above the threshold
 *          (default 0.40) is a near-duplicate and fails the run.
 *        - Lockfiles (package-lock.json, npm-shrinkwrap.json, yarn.lock,
 *          pnpm-lock.yaml) are generated, never scored, and listed as excluded.
 *      There is no allowlist by file name.
 *
 * Usage
 *   node scripts/check-theme-free.mjs [--json] [--themes-dir DIR] [--frontend-dir DIR]
 *                                     [--similarity-threshold N]
 *
 *   --themes-dir            default <repo>/themes/React/StarterKit (must contain TS/ and JS/)
 *   --frontend-dir          default <repo>/frontend
 *   --similarity-threshold  near-duplicate cut-off, a number in (0, 1], default 0.4
 *   --json                  print one JSON object on stdout instead of the human report
 *
 *   The repo root is resolved from this script's own location, so the current
 *   working directory does not matter.
 *
 * Exit codes
 *   0  no identical file, no string hit and no near-duplicate
 *   1  at least one identical file, string hit or near-duplicate
 *   2  a kit dir (TS or JS) or the frontend dir is missing, not a directory or
 *      empty after exclusions; a usage error; also any unexpected error
 *
 * Notes
 *   - Directories named node_modules, dist or .vite are skipped at any depth, in
 *     the frontend and in the kits. Symbolic links are never followed.
 *   - Files whose trimmed content is under 64 bytes are never compared (too
 *     generic to mean anything) and are listed separately as ignored-trivial.
 *   - This script lives outside frontend/ so it never matches its own scan.
 *   - themes/ is gitignored (local only). A clean clone has no kits, so the
 *     verifier exits 2 there instead of reporting a false clean result.
 */

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { createHash } from 'node:crypto'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const EXCLUDED_DIRS = new Set(['node_modules', 'dist', '.vite'])
const TRIVIAL_BYTES = 64
const KITS = ['TS', 'JS']
const STRING_PATTERN = /ubold|coderthemes/gi
const BINARY_SNIFF_BYTES = 8000
const MAX_TEXT = 160

// Near-duplicate check (see the header for the rules).
const DEFAULT_SIMILARITY_THRESHOLD = 0.4
const MIN_UNITS = 8
const MIN_LINE_CHARS = 12
const UBIQUITOUS_KIT_FILES = 10
const SHINGLE_SIZE = 5
const TOP_COUNT = 10
const LOCKFILES = new Set(['package-lock.json', 'npm-shrinkwrap.json', 'yarn.lock', 'pnpm-lock.yaml'])
const USAGE =
  'usage: node scripts/check-theme-free.mjs [--json] [--themes-dir DIR] [--frontend-dir DIR] [--similarity-threshold N]'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

class UsageError extends Error {}

function parseThreshold(value) {
  if (value === undefined || value.startsWith('--')) {
    throw new UsageError('--similarity-threshold requires a number in (0, 1]')
  }
  const n = Number(value)
  if (value.trim() === '' || !Number.isFinite(n) || n <= 0 || n > 1) {
    throw new UsageError(`--similarity-threshold must be a number in (0, 1], got: ${value}`)
  }
  return n
}

function parseArgs(argv) {
  const opts = {
    json: false,
    themesDir: join(repoRoot, 'themes', 'React', 'StarterKit'),
    frontendDir: join(repoRoot, 'frontend'),
    similarityThreshold: DEFAULT_SIMILARITY_THRESHOLD,
  }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--json') {
      opts.json = true
    } else if (arg === '--similarity-threshold') {
      opts.similarityThreshold = parseThreshold(argv[++i])
    } else if (arg === '--themes-dir' || arg === '--frontend-dir') {
      const value = argv[++i]
      if (value === undefined || value.startsWith('--')) {
        throw new UsageError(`${arg} requires a directory argument`)
      }
      if (arg === '--themes-dir') opts.themesDir = resolve(value)
      else opts.frontendDir = resolve(value)
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

/** Recursively collect regular files (absolute paths), sorted, exclusions applied. */
function walk(root) {
  const out = []
  const visit = (dir) => {
    const entries = readdirSync(dir, { withFileTypes: true })
    entries.sort((a, b) => byCodeUnit(a.name, b.name))
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) {
        if (EXCLUDED_DIRS.has(entry.name)) continue
        visit(full)
      } else if (entry.isFile()) {
        out.push(full)
      }
    }
  }
  visit(root)
  return out
}

function isDirectory(p) {
  try {
    return statSync(p).isDirectory()
  } catch {
    return false
  }
}

class MissingInputError extends Error {}

/** Walk a required input dir; throws MissingInputError when missing/not a dir/empty. */
function walkRequired(dir) {
  if (!isDirectory(dir)) throw new MissingInputError(dir)
  const files = walk(dir)
  if (files.length === 0) throw new MissingInputError(dir)
  return files
}

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex')
}

function isTrivial(buffer) {
  return buffer.toString('utf8').trim().length < TRIVIAL_BYTES
}

/** Group key: directory path truncated to at most 3 segments; root files -> "(root)". */
function groupOf(relPath) {
  const parts = relPath.split('/')
  parts.pop()
  if (parts.length === 0) return '(root)'
  return parts.slice(0, 3).join('/')
}

function buildKitIndex(themesDir) {
  const index = new Map()
  const kitDirs = []
  const kitCounts = []
  const textFiles = []
  for (const kit of KITS) {
    const kitDir = join(themesDir, kit)
    const files = walkRequired(kitDir)
    kitDirs.push(kitDir)
    kitCounts.push(files.length)
    for (const file of files) {
      const buffer = readFileSync(file)
      const label = `${kit}/${toPosix(relative(kitDir, file))}`
      if (!isBinary(buffer)) textFiles.push({ label, lines: normalizeLines(buffer.toString('utf8')) })
      if (isTrivial(buffer)) continue
      const hash = sha256(buffer)
      const list = index.get(hash)
      if (list) list.push(label)
      else index.set(hash, [label])
    }
  }
  return { index, kitDirs, kitCounts, similarity: buildSimilarityIndex(textFiles) }
}

function isBinary(buffer) {
  return buffer.subarray(0, BINARY_SNIFF_BYTES).includes(0)
}

// ---------------------------------------------------------------------------
// Near-duplicate scoring
// ---------------------------------------------------------------------------

/**
 * Removes comments while respecting quoted strings, so a glob such as
 * '**' + '/*.ts' or a URL inside quotes is never read as a comment opener.
 * Newlines inside removed comments are kept, so line structure survives.
 * Single and double quotes end at a newline (a stray apostrophe in prose
 * cannot swallow the rest of a file); template literals may span lines.
 */
function stripComments(text) {
  let out = ''
  let i = 0
  let quote = null
  const n = text.length
  // An opener without a closer is kept as literal text, so a stray `/*` (a
  // glob in an ignore file, a path in a config comment) can never swallow the
  // rest of the file and hide it from scoring.
  const skipUntil = (closer) => {
    const end = text.indexOf(closer, i + 2)
    if (end === -1) return false
    const stop = end + closer.length
    out += text.slice(i, stop).replace(/[^\n]/g, '')
    i = stop
    return true
  }
  const atLineStart = (pos) => {
    for (let k = pos - 1; k >= 0; k--) {
      if (text[k] === '\n' || text[k] === '\r') return true
      if (text[k] !== ' ' && text[k] !== '\t') return false
    }
    return true
  }
  while (i < n) {
    const ch = text[i]
    if (quote) {
      out += ch
      if (ch === '\\' && i + 1 < n) {
        out += text[i + 1]
        i += 2
        continue
      }
      if (ch === quote || (ch === '\n' && quote !== '`')) quote = null
      i++
      continue
    }
    // A line starting with '#' (shell, nginx, YAML and ignore-file comments) is
    // dropped by the line rules anyway; skipping it here also keeps the comment
    // markers it may contain from opening a block.
    if (ch === '#' && atLineStart(i)) {
      const end = text.indexOf('\n', i)
      i = end === -1 ? n : end
      continue
    }
    if (ch === '/' && text[i + 1] === '*' && skipUntil('*/')) continue
    if (ch === '<' && text.startsWith('<!--', i) && skipUntil('-->')) continue
    if (ch === '/' && text[i + 1] === '/') {
      const prev = i === 0 ? '\n' : text[i - 1]
      if (prev === '\n' || prev === '\r' || prev === ' ' || prev === '\t') {
        const end = text.indexOf('\n', i)
        i = end === -1 ? n : end
        continue
      }
    }
    if (ch === "'" || ch === '"' || ch === '`') quote = ch
    out += ch
    i++
  }
  return out
}

const IMPORT_STATEMENT =
  /^[ \t]*import\s+(?:type\s+)?(?:[\w$]+\s*,?\s*)?(?:\{[^}]*\}|\*\s*as\s+[\w$]+)?\s*from\s*(['"])[^'"\n]*\1[ \t]*;?/gm
const REEXPORT_STATEMENT =
  /^[ \t]*export\s+(?:type\s+)?(?:\{[^}]*\}|\*(?:\s*as\s+[\w$]+)?)\s*from\s*(['"])[^'"\n]*\1[ \t]*;?/gm

function isDroppedLine(line) {
  return (
    line.length < MIN_LINE_CHARS ||
    line.startsWith('#') ||
    /^import(?:\s|['"{*])/.test(line) ||
    /^export\b.*\bfrom\s*['"]/.test(line) ||
    /^@(?:use|import|forward)\b/.test(line)
  )
}

/** Normalized, ordered lines of a text file (duplicates kept, for shingles). */
function normalizeLines(text) {
  const code = stripComments(text)
    .replace(IMPORT_STATEMENT, (m) => m.replace(/[^\n]/g, ''))
    .replace(REEXPORT_STATEMENT, (m) => m.replace(/[^\n]/g, ''))
  const lines = []
  for (const raw of code.split(/\r\n|\r|\n/)) {
    const line = raw.replace(/\s+/g, ' ').trim()
    if (!isDroppedLine(line)) lines.push(line)
  }
  return lines
}

function shingles(lines) {
  const set = new Set()
  for (let i = 0; i + SHINGLE_SIZE <= lines.length; i++) {
    set.add(lines.slice(i, i + SHINGLE_SIZE).join('\n'))
  }
  return set
}

function addToIndex(index, unit, id) {
  const list = index.get(unit)
  if (list) list.push(id)
  else index.set(unit, [id])
}

/** Inverted indexes (line -> kit file ids, shingle -> kit file ids) plus the ubiquitous-line set. */
function buildSimilarityIndex(textFiles) {
  const fileCountByLine = new Map()
  const lineSets = textFiles.map((f) => new Set(f.lines))
  for (const set of lineSets) {
    for (const line of set) fileCountByLine.set(line, (fileCountByLine.get(line) ?? 0) + 1)
  }
  const ubiquitous = new Set()
  for (const [line, count] of fileCountByLine) if (count >= UBIQUITOUS_KIT_FILES) ubiquitous.add(line)

  const labels = textFiles.map((f) => f.label)
  const lineIndex = new Map()
  const shingleIndex = new Map()
  textFiles.forEach((f, id) => {
    for (const line of lineSets[id]) if (!ubiquitous.has(line)) addToIndex(lineIndex, line, id)
    for (const s of shingles(f.lines.filter((l) => !ubiquitous.has(l)))) addToIndex(shingleIndex, s, id)
  })
  return { labels, ubiquitous, lineIndex, shingleIndex }
}

function round4(x) {
  return Math.round(x * 10000) / 10000
}

/** Scores one frontend file against its closest kit file. */
function scoreFile(rel, text, sim) {
  const isJson = rel.toLowerCase().endsWith('.json')
  const lines = normalizeLines(text).filter((l) => !sim.ubiquitous.has(l))
  const units = isJson ? shingles(lines) : new Set(lines)
  const index = isJson ? sim.shingleIndex : sim.lineIndex
  const sharedById = new Map()
  for (const unit of units) {
    for (const id of index.get(unit) ?? []) sharedById.set(id, (sharedById.get(id) ?? 0) + 1)
  }
  let bestId = -1
  let bestShared = 0
  for (const [id, shared] of sharedById) {
    if (
      shared > bestShared ||
      (shared === bestShared && bestId !== -1 && byCodeUnit(sim.labels[id], sim.labels[bestId]) < 0)
    ) {
      bestId = id
      bestShared = shared
    }
  }
  const ratio = units.size === 0 ? 0 : bestShared / units.size
  return {
    file: rel,
    kitFile: bestId === -1 ? null : sim.labels[bestId],
    score: round4(ratio),
    ratio,
    shared: bestShared,
    units: units.size,
    metric: isJson ? 'shingles5' : 'lines',
  }
}

function byScoreDesc(a, b) {
  return b.ratio - a.ratio || byCodeUnit(a.file, b.file)
}

function publicEntry({ ratio: _ratio, ...rest }) {
  return rest
}

function summarizeSimilarity(scored, tooSmall, excluded, threshold) {
  scored.sort(byScoreDesc)
  const offenders = scored.filter((e) => e.ratio >= threshold)
  tooSmall.sort((a, b) => byCodeUnit(a.file, b.file))
  excluded.sort(byCodeUnit)
  return {
    threshold,
    minUnits: MIN_UNITS,
    metric: { default: 'lines', json: 'shingles5' },
    count: offenders.length,
    files: offenders.map(publicEntry),
    top: scored.slice(0, TOP_COUNT).map(publicEntry),
    tooSmall: { count: tooSmall.length, files: tooSmall.map(publicEntry) },
    excluded,
  }
}

/** Case-insensitive search for the template's vendor/product names, per line. */
function findStringHits(rel, buffer) {
  if (isBinary(buffer)) return []
  const hits = []
  const lines = buffer.toString('utf8').split(/\r\n|\r|\n/)
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    for (const m of line.matchAll(STRING_PATTERN)) {
      hits.push({ file: rel, line: i + 1, match: m[0], text: line.trim().slice(0, MAX_TEXT) })
    }
  }
  return hits
}

function scan(opts) {
  const frontendFiles = walkRequired(opts.frontendDir)
  const { index, kitDirs, kitCounts, similarity } = buildKitIndex(opts.themesDir)

  const identical = []
  const trivial = []
  const byGroup = {}
  const stringHits = []
  const scored = []
  const tooSmall = []
  const excluded = []

  for (const file of frontendFiles) {
    const rel = toPosix(relative(opts.frontendDir, file))
    const buffer = readFileSync(file)
    stringHits.push(...findStringHits(rel, buffer))
    if (!isBinary(buffer)) {
      const base = rel.slice(rel.lastIndexOf('/') + 1)
      if (LOCKFILES.has(base)) {
        excluded.push(rel)
      } else {
        const entry = scoreFile(rel, buffer.toString('utf8'), similarity)
        if (entry.units < MIN_UNITS) tooSmall.push(entry)
        else scored.push(entry)
      }
    }
    if (isTrivial(buffer)) {
      trivial.push(rel)
      continue
    }
    const matches = index.get(sha256(buffer))
    if (matches) {
      identical.push({ file: rel, kitMatches: [...matches] })
      const group = groupOf(rel)
      byGroup[group] = (byGroup[group] ?? 0) + 1
    }
  }

  trivial.sort(byCodeUnit)
  const similar = summarizeSimilarity(scored, tooSmall, excluded, opts.similarityThreshold)

  const result = {
    ok: identical.length === 0 && stringHits.length === 0 && similar.count === 0,
    frontendDir: toPosix(opts.frontendDir),
    kitDirs: kitDirs.map(toPosix),
    scanned: frontendFiles.length,
    identical: { count: identical.length, files: identical, byGroup },
    ignoredTrivial: { count: trivial.length, files: trivial },
    stringHits: {
      count: stringHits.length,
      fileCount: new Set(stringHits.map((h) => h.file)).size,
      hits: stringHits,
    },
    similar,
  }
  return { result, kitCounts }
}

function similarityLine(entry) {
  const pct = `${Math.round(entry.score * 100)}%`.padStart(4)
  const kit = entry.kitFile ?? '(no shared unit)'
  const metric = entry.metric === 'lines' ? '' : `  [${entry.metric}]`
  return `  ${entry.file}  ${pct}  ${entry.shared}/${entry.units}  ${kit}${metric}`
}

function printHuman({ result, kitCounts }) {
  const lines = []
  lines.push('check-theme-free — StarterKit identity check')
  lines.push(`Frontend: ${result.frontendDir} (${result.scanned} files)`)
  lines.push(`Kits: ${result.kitDirs[0]} (${kitCounts[0]} files), ${result.kitDirs[1]} (${kitCounts[1]} files)`)

  lines.push(`Identical files: ${result.identical.count}`)
  const groups = Object.entries(result.identical.byGroup).sort(
    (a, b) => b[1] - a[1] || byCodeUnit(a[0], b[0]),
  )
  for (const [group, count] of groups) {
    lines.push(`  ${group} (${count})`)
    for (const entry of result.identical.files) {
      if (groupOf(entry.file) !== group) continue
      const more = entry.kitMatches.length > 1 ? ` (+${entry.kitMatches.length - 1} more)` : ''
      lines.push(`    ${entry.file}  =  ${entry.kitMatches[0]}${more}`)
    }
  }

  lines.push(`Ignored (trivial, <64 bytes): ${result.ignoredTrivial.count}`)
  for (const file of result.ignoredTrivial.files) lines.push(`  ${file}`)

  lines.push(
    `String hits (ubold|coderthemes): ${result.stringHits.count} in ${result.stringHits.fileCount} files`,
  )
  for (const hit of result.stringHits.hits) lines.push(`  ${hit.file}:${hit.line}  ${hit.text}`)

  const sim = result.similar
  lines.push(`Near-duplicates (>= ${Math.round(sim.threshold * 100)}%): ${sim.count}`)
  for (const entry of sim.files) lines.push(similarityLine(entry))
  lines.push(`Top similarity scores: (files with >= ${sim.minUnits} units; ${sim.tooSmall.count} smaller files not judged)`)
  for (const entry of sim.top) lines.push(similarityLine(entry))
  lines.push(`Excluded from similarity (lockfiles): ${sim.excluded.length}`)
  for (const file of sim.excluded) lines.push(`  ${file}`)

  lines.push(`Result: ${result.ok ? 'PASS' : 'FAIL'}`)
  process.stdout.write(lines.join('\n') + '\n')
}

function main() {
  const opts = parseArgs(process.argv.slice(2))
  const report = scan(opts)
  if (opts.json) {
    process.stdout.write(JSON.stringify(report.result, null, 2) + '\n')
  } else {
    printHuman(report)
  }
  process.exitCode = report.result.ok ? 0 : 1
}

try {
  main()
} catch (err) {
  if (err instanceof MissingInputError) {
    process.stderr.write(
      `check-theme-free: ${err.message} is missing or empty — cannot certify theme-free without both StarterKit kits (themes/ is gitignored, local only)\n`,
    )
  } else if (err instanceof UsageError) {
    process.stderr.write(`check-theme-free: ${err.message}\n${USAGE}\n`)
  } else {
    process.stderr.write(`check-theme-free: ${err && err.message ? err.message : String(err)}\n`)
  }
  process.exitCode = 2
}
