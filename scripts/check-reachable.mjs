#!/usr/bin/env node
/**
 * check-reachable.mjs — import-graph reachability check for the SimpleFinance frontend.
 *
 * Purpose
 *   Walks the module graph from the app entry (src/main.tsx by default) and lists
 *   every file under the src dir that nothing reaches: source files, stylesheets,
 *   locale JSON, images and fonts. It is the gate used when deleting code: a file
 *   it reports as unreachable can be removed without breaking the bundle, and a
 *   local import it cannot resolve is reported as a failure.
 *
 *   Followed specifier forms
 *     .ts .tsx .js .jsx  static `import ... from`, side-effect `import '...'`,
 *                        `export ... from`, dynamic `import('...')` and
 *                        `import.meta.glob('pattern', ...)` (a `*` matches one
 *                        path segment, `**` any number of segments)
 *     .scss .css         `@import`, `@use`, `@forward` (comma lists included)
 *   `@/x` resolves against the alias root, `./x` and `../x` against the importing
 *   file. Anything else is a package and is skipped, except a style specifier that
 *   resolves to a local file or partial. Comments are stripped before scanning, so
 *   a commented-out import does not count.
 *
 * Usage
 *   node scripts/check-reachable.mjs [--json] [--src-dir DIR] [--entry FILE] [--alias-root DIR]
 *
 *   --src-dir     default <repo>/frontend/src
 *   --entry       default <src-dir>/main.tsx
 *   --alias-root  default <src-dir> (the directory the `@/` prefix maps to)
 *   --json        print one JSON object on stdout instead of the human report:
 *                 { ok, entry, srcDir, reachableCount, unreachable: [path],
 *                   unresolved: [{ from, specifier }] }
 *                 Paths are relative to the src dir, forward slashes, sorted.
 *
 *   The repo root is resolved from this script's own location, so the current
 *   working directory does not matter.
 *
 * Exit codes
 *   0  every file is reachable and every local specifier resolves
 *   1  at least one unreachable file or unresolved local specifier
 *   2  the src dir or the entry file is missing, a usage error, or any
 *      unexpected error
 *
 * Notes
 *   - Directories named node_modules are skipped. Symbolic links are never followed.
 *   - Ambient declaration files (*.d.ts) always count as reachable: they are
 *     picked up by the compiler, never imported.
 *   - JSON, images and fonts are leaves: they are marked reachable, not parsed.
 */

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const EXCLUDED_DIRS = new Set(['node_modules'])
const CODE_EXTS = ['.ts', '.tsx', '.js', '.jsx']
const STYLE_EXTS = ['.scss', '.css']
const RESOLVE_EXTS = ['.ts', '.tsx', '.js', '.jsx', '.json', '.scss', '.css']
const ALIAS_PREFIX = '@/'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

class UsageError extends Error {}
class MissingInputError extends Error {}

function parseArgs(argv) {
  const opts = { json: false, srcDir: null, entry: null, aliasRoot: null }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--json') {
      opts.json = true
    } else if (arg === '--src-dir' || arg === '--entry' || arg === '--alias-root') {
      const value = argv[++i]
      if (value === undefined || value.startsWith('--')) {
        throw new UsageError(`${arg} requires a path argument`)
      }
      if (arg === '--src-dir') opts.srcDir = resolve(value)
      else if (arg === '--entry') opts.entry = resolve(value)
      else opts.aliasRoot = resolve(value)
    } else {
      throw new UsageError(`unknown argument: ${arg}`)
    }
  }
  opts.srcDir ??= join(repoRoot, 'frontend', 'src')
  opts.entry ??= join(opts.srcDir, 'main.tsx')
  opts.aliasRoot ??= opts.srcDir
  return opts
}

function toPosix(p) {
  return p.split(sep).join('/')
}

function byCodeUnit(a, b) {
  return a < b ? -1 : a > b ? 1 : 0
}

function isFile(p) {
  try {
    return statSync(p).isFile()
  } catch {
    return false
  }
}

function isDirectory(p) {
  try {
    return statSync(p).isDirectory()
  } catch {
    return false
  }
}

function hasExt(file, exts) {
  const lower = file.toLowerCase()
  return exts.some((ext) => lower.endsWith(ext))
}

/** Recursively collect regular files (absolute paths) under root, node_modules skipped. */
function walkFiles(root) {
  const out = []
  const visit = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isSymbolicLink()) continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) {
        if (!EXCLUDED_DIRS.has(entry.name)) visit(full)
      } else if (entry.isFile()) {
        out.push(full)
      }
    }
  }
  visit(root)
  return out
}

/**
 * Remove // and block comments while keeping string literals intact. Strings are
 * matched first, so a glob such as './locales/*\/*.json' never opens a comment.
 * Quote strings stop at a line break, so a stray apostrophe in JSX text cannot
 * swallow the rest of the file.
 */
const TOKEN_RE =
  /('(?:\\.|[^'\\\r\n])*'|"(?:\\.|[^"\\\r\n])*"|`(?:\\[\s\S]|[^`\\])*`)|\/\/[^\r\n]*|\/\*[\s\S]*?\*\//g

function stripComments(text) {
  return text.replace(TOKEN_RE, (match, str) => (str !== undefined ? str : ' '))
}

const FROM_RE = /\bfrom\s*(['"])([^'"\r\n]+)\1/g
const SIDE_EFFECT_RE = /\bimport\s*(['"])([^'"\r\n]+)\1/g
const DYNAMIC_RE = /\bimport\s*\(\s*(['"`])([^'"`\r\n]+)\1\s*\)/g
const GLOB_RE = /\bimport\.meta\.glob\s*(?:<[^>]*>)?\s*\(\s*(['"`])([^'"`\r\n]+)\1/g
const STYLE_RULE_RE = /@(?:import|use|forward)\s+([^;]+);/g
const QUOTED_RE = /(['"])([^'"\r\n]+)\1/g

/** Returns { specifiers: [string], globs: [string] } for a code file. */
function scanCode(text) {
  const code = stripComments(text)
  const specifiers = []
  const globs = []
  for (const re of [FROM_RE, SIDE_EFFECT_RE, DYNAMIC_RE]) {
    for (const m of code.matchAll(re)) specifiers.push(m[2])
  }
  for (const m of code.matchAll(GLOB_RE)) globs.push(m[2])
  return { specifiers, globs }
}

/** Returns the quoted specifiers of every @import/@use/@forward rule. */
function scanStyle(text) {
  const css = stripComments(text)
  const specifiers = []
  for (const rule of css.matchAll(STYLE_RULE_RE)) {
    for (const q of rule[1].matchAll(QUOTED_RE)) specifiers.push(q[2])
  }
  return specifiers
}

function isLocalSpecifier(spec) {
  return spec.startsWith(ALIAS_PREFIX) || spec.startsWith('./') || spec.startsWith('../')
}

function basePathOf(spec, fromFile, aliasRoot) {
  if (spec.startsWith(ALIAS_PREFIX)) return join(aliasRoot, spec.slice(ALIAS_PREFIX.length))
  return resolve(dirname(fromFile), spec)
}

/** Resolve a base path to an existing file, or null. */
function resolveFile(base, style) {
  const candidates = [base]
  for (const ext of RESOLVE_EXTS) candidates.push(base + ext)
  if (style) {
    const partial = join(dirname(base), `_${base.slice(dirname(base).length + 1)}`)
    candidates.push(partial)
    for (const ext of STYLE_EXTS) candidates.push(partial + ext)
  }
  for (const ext of RESOLVE_EXTS) candidates.push(join(base, `index${ext}`))
  if (style) {
    for (const ext of STYLE_EXTS) candidates.push(join(base, `_index${ext}`))
  }
  for (const candidate of candidates) {
    if (isFile(candidate)) return candidate
  }
  return null
}

function escapeRegex(text) {
  return text.replace(/[.+?^${}()|[\]\\]/g, '\\$&')
}

/** Absolute posix glob (from a relative pattern) to an anchored RegExp. */
function globToRegex(absPattern) {
  let out = ''
  const segments = absPattern.split('/')
  for (let i = 0; i < segments.length; i++) {
    const segment = segments[i]
    const last = i === segments.length - 1
    if (segment === '**') {
      out += last ? '.*' : '(?:[^/]+/)*'
      continue
    }
    out += segment
      .split('*')
      .map(escapeRegex)
      .join('[^/]*')
    if (!last) out += '/'
  }
  return new RegExp(`^${out}$`)
}

function check(opts) {
  if (!isDirectory(opts.srcDir)) throw new MissingInputError(`src dir ${opts.srcDir} is missing or not a directory`)
  if (!isFile(opts.entry)) throw new MissingInputError(`entry file ${opts.entry} is missing`)

  const files = walkFiles(opts.srcDir)
  const filesPosix = files.map(toPosix)
  const reachable = new Set()
  const unresolved = []
  const queue = [opts.entry]

  const rel = (file) => toPosix(relative(opts.srcDir, file))

  const mark = (file) => {
    if (reachable.has(file)) return
    reachable.add(file)
    queue.push(file)
  }

  reachable.add(opts.entry)

  while (queue.length > 0) {
    const file = queue.shift()
    const isCode = hasExt(file, CODE_EXTS)
    const isStyle = hasExt(file, STYLE_EXTS)
    if (!isCode && !isStyle) continue

    const text = readFileSync(file, 'utf8')

    if (isCode) {
      const { specifiers, globs } = scanCode(text)
      for (const raw of specifiers) {
        const spec = raw.split('?')[0]
        if (!isLocalSpecifier(spec)) continue
        const target = resolveFile(basePathOf(spec, file, opts.aliasRoot), hasExt(spec, STYLE_EXTS))
        if (target) mark(target)
        else unresolved.push({ from: rel(file), specifier: raw })
      }
      for (const pattern of globs) {
        if (!isLocalSpecifier(pattern)) continue
        const matcher = globToRegex(toPosix(basePathOf(pattern, file, opts.aliasRoot)))
        filesPosix.forEach((p, i) => {
          if (matcher.test(p)) mark(files[i])
        })
      }
    } else {
      for (const raw of scanStyle(text)) {
        const spec = raw.split('?')[0]
        const local = isLocalSpecifier(spec)
        const base = local ? basePathOf(spec, file, opts.aliasRoot) : resolve(dirname(file), spec)
        if (!local && (spec.includes(':') || spec.startsWith('/'))) continue
        const target = resolveFile(base, true)
        if (target) mark(target)
        else if (local) unresolved.push({ from: rel(file), specifier: raw })
      }
    }
  }

  for (const file of files) {
    if (file.toLowerCase().endsWith('.d.ts')) reachable.add(file)
  }

  const unreachable = files.filter((f) => !reachable.has(f)).map(rel).sort(byCodeUnit)
  unresolved.sort((a, b) => byCodeUnit(a.from, b.from) || byCodeUnit(a.specifier, b.specifier))

  return {
    ok: unreachable.length === 0 && unresolved.length === 0,
    entry: rel(opts.entry),
    srcDir: toPosix(opts.srcDir),
    reachableCount: files.filter((f) => reachable.has(f)).length,
    unreachable,
    unresolved,
  }
}

function printHuman(result) {
  const lines = []
  lines.push('check-reachable — import graph from the app entry')
  lines.push(`Src dir: ${result.srcDir}`)
  lines.push(`Entry: ${result.entry}`)
  lines.push(`Reachable files: ${result.reachableCount}`)
  lines.push(`Unreachable files: ${result.unreachable.length}`)
  for (const file of result.unreachable) lines.push(`  ${file}`)
  lines.push(`Unresolved local specifiers: ${result.unresolved.length}`)
  for (const u of result.unresolved) lines.push(`  ${u.from}  ->  ${u.specifier}`)
  lines.push(`Result: ${result.ok ? 'PASS' : 'FAIL'}`)
  process.stdout.write(lines.join('\n') + '\n')
}

function main() {
  const opts = parseArgs(process.argv.slice(2))
  const result = check(opts)
  if (opts.json) process.stdout.write(JSON.stringify(result, null, 2) + '\n')
  else printHuman(result)
  process.exitCode = result.ok ? 0 : 1
}

try {
  main()
} catch (err) {
  process.stderr.write(`check-reachable: ${err && err.message ? err.message : String(err)}\n`)
  process.exitCode = 2
}
