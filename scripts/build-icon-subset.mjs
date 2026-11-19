#!/usr/bin/env node
/**
 * build-icon-subset.mjs — writes the lucide icons the web client uses into one
 * committed JSON file, so icons render from the bundle with no request to a
 * public icon API.
 *
 * What it reads
 *   - the full lucide set from the @iconify-json/lucide dev dependency
 *     (icons.json, plus the sibling info.json for the name/author/license block)
 *   - every .ts/.tsx file under the src dir; only string literals are looked at,
 *     comments are skipped, and template literals with `${` are dynamic and ignored
 *
 * Which literals are icon names
 *   - 'lucide:<name>'                    always required: it must exist in the set
 *   - '<set>:<name>' for another set     fails ("only lucide is bundled") when it sits in
 *                                        an icon position or names a known icon set;
 *                                        other 'ns:key' literals are i18n keys and ignored
 *   - a bare kebab name ('arrow-up')     required in an icon position, otherwise
 *                                        included only when the set has it
 *   - a PascalCase name ('HeartPulse')   mapped through the slug rule below, same
 *                                        required/optional split
 *   Icon positions: a JSX `icon="..."` value, an `icon: '...'` property, a literal
 *   directly inside `icon={...}` as the whole value, a branch of `cond ? 'a' : 'b'` or
 *   a `||`/`??` fallback (an operand such as `state === 'done'` is not an icon), and
 *   every entry of an array assigned to a `*ICON_NAMES` constant (the category
 *   icon picker, whose stored PascalCase values are the wire format).
 *
 * Slug rule (same as toIconifySlug in frontend/src/features/categories/icons.ts)
 *   hyphen before an uppercase letter that follows a lowercase letter or digit,
 *   hyphen between a letter and a digit, then lowercase:
 *   HeartPulse -> heart-pulse, Building2 -> building-2.
 *
 * Aliases
 *   A used name that the set lists under `aliases` is copied to `aliases`, and
 *   its parent chain is followed until a real icon, which is copied to `icons`.
 *
 * Output
 *   IconifyJSON { aliases, height, icons, info, prefix, width }, keys sorted at
 *   every level, two-space indent, trailing newline. The same inputs always give
 *   the same bytes.
 *
 * Usage
 *   node scripts/build-icon-subset.mjs [--source FILE] [--src-dir DIR] [--out FILE] [--check]
 *
 *   --source   default <repo>/frontend/node_modules/@iconify-json/lucide/icons.json
 *   --src-dir  default <repo>/frontend/src
 *   --out      default <repo>/frontend/src/components/icons/lucide-subset.json
 *   --check    do not write; compare the committed file with a fresh generation
 *              (parsed content, so CRLF checkouts are not reported as stale)
 *
 * Exit codes
 *   0  subset written, or --check found it current
 *   1  an icon name does not resolve, another icon set is referenced, or
 *      --check found the committed file stale or absent (missing/extra names listed)
 *   2  the source set or its license info is missing (run npm install in
 *      frontend/), the src dir is missing, or a usage error
 */

import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const DEFAULTS = {
  source: join(REPO, 'frontend', 'node_modules', '@iconify-json', 'lucide', 'icons.json'),
  srcDir: join(REPO, 'frontend', 'src'),
  out: join(REPO, 'frontend', 'src', 'components', 'icons', 'lucide-subset.json'),
}

const BUNDLED_SET = 'lucide'
/** Icon set prefixes that are never i18n namespaces; a literal using one is a foreign icon. */
const FOREIGN_SETS = new Set([
  'mdi', 'mdi-light', 'tabler', 'ph', 'bi', 'ri', 'fa', 'fa6-solid', 'fa6-regular', 'fa-solid', 'fa-regular',
  'material-symbols', 'ic', 'heroicons', 'heroicons-outline', 'heroicons-solid', 'carbon', 'ion', 'uil',
  'solar', 'mingcute', 'iconoir', 'feather', 'octicon', 'bx', 'bxs', 'bxl', 'simple-icons', 'logos', 'twemoji',
  'fluent', 'akar-icons', 'eva', 'gg', 'line-md', 'majesticons', 'radix-icons', 'teenyicons', 'circum', 'lets-icons',
])

const KEBAB = /^[a-z0-9]+(?:-[a-z0-9]+)*$/
const PASCAL = /^[A-Z][a-zA-Z0-9]*$/
const PREFIXED = /^([a-z0-9]+(?:-[a-z0-9]+)*):([a-z0-9]+(?:-[a-z0-9]+)*)$/

const ICON_ATTR = /(?<![\w-])icon=\s*$/
const ICON_PROP = /(?<![\w-])icon\s*:\s*$/
// Inside icon={...}: the whole value, a ternary branch or a fallback (after `{`, `?`, `:`, `||`, `??`),
// but not an operand such as the 'done' in icon={state === 'done' ? 'check' : 'x'}.
const ICON_EXPR = /(?<![\w-])icon=\{[^{}]*(?:[{?:]|\|\|)\s*$/
const ICON_LIST = /\b[A-Z0-9_]*ICON_NAMES\b[^=;\n]*=\s*\[/g
const LOOKBACK = 200

class UsageError extends Error {}
class SetupError extends Error {}

/** PascalCase -> lucide kebab name. */
export function toSlug(name) {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/([A-Za-z])([0-9])/g, '$1-$2')
    .toLowerCase()
}

/**
 * Returns the string literals of a TS/TSX source with their offsets. Comments
 * are skipped. Quoted strings end at a newline (an apostrophe in JSX text then
 * costs one line at most). Template literals holding `${` are dropped, but the
 * code inside the placeholder is still scanned.
 */
export function scanLiterals(text) {
  const found = []
  const n = text.length
  let i = 0

  const readQuoted = (quote) => {
    const start = i
    let value = ''
    i++
    while (i < n) {
      const c = text[i]
      if (c === '\\') {
        value += text[i + 1] ?? ''
        i += 2
      } else if (c === '\n') {
        return
      } else if (c === quote) {
        i++
        found.push({ value, start })
        return
      } else {
        value += c
        i++
      }
    }
  }

  const readTemplate = () => {
    const start = i
    let value = ''
    let dynamic = false
    i++
    while (i < n) {
      const c = text[i]
      if (c === '\\') {
        value += text[i + 1] ?? ''
        i += 2
      } else if (c === '`') {
        i++
        if (!dynamic) found.push({ value, start })
        return
      } else if (c === '$' && text[i + 1] === '{') {
        dynamic = true
        i += 2
        scanCode(true)
      } else {
        value += c
        i++
      }
    }
  }

  function scanCode(untilClosingBrace) {
    let depth = 0
    while (i < n) {
      const c = text[i]
      const next = text[i + 1]
      if (c === '/' && next === '/') {
        while (i < n && text[i] !== '\n') i++
      } else if (c === '/' && next === '*') {
        const end = text.indexOf('*/', i + 2)
        i = end < 0 ? i + 2 : end + 2
      } else if (c === "'" || c === '"') {
        readQuoted(c)
      } else if (c === '`') {
        readTemplate()
      } else {
        if (untilClosingBrace) {
          if (c === '{') depth++
          else if (c === '}') {
            if (depth === 0) {
              i++
              return
            }
            depth--
          }
        }
        i++
      }
    }
  }

  scanCode(false)
  return found
}

/** Offsets ranges [open, close] of arrays assigned to *ICON_NAMES constants. */
function iconListRanges(text) {
  const ranges = []
  for (const match of text.matchAll(ICON_LIST)) {
    const open = match.index + match[0].length - 1
    const close = text.indexOf(']', open)
    ranges.push([open, close < 0 ? text.length : close])
  }
  return ranges
}

function isIconPosition(text, start, ranges) {
  const before = text.slice(Math.max(0, start - LOOKBACK), start)
  if (ICON_ATTR.test(before) || ICON_PROP.test(before) || ICON_EXPR.test(before)) return true
  return ranges.some(([open, close]) => start > open && start < close)
}

function lineOf(text, offset) {
  let line = 1
  for (let k = 0; k < offset; k++) if (text.charCodeAt(k) === 10) line++
  return line
}

function walk(dir, files = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue
    const full = join(dir, entry.name)
    if (entry.isDirectory()) walk(full, files)
    else if (entry.isFile() && /\.tsx?$/.test(entry.name)) files.push(full)
  }
  return files.sort()
}

/** Resolves a name to the chain of entries it needs: [{ kind: 'alias'|'icon', name }], or null. */
function resolveChain(set, name) {
  const chain = []
  const seen = new Set()
  let current = name
  while (!seen.has(current)) {
    seen.add(current)
    if (set.icons[current]) {
      chain.push({ kind: 'icon', name: current })
      return chain
    }
    const alias = set.aliases?.[current]
    if (!alias || typeof alias.parent !== 'string') return null
    chain.push({ kind: 'alias', name: current })
    current = alias.parent
  }
  return null
}

/**
 * Scans the src dir and returns { needed: Set<name>, errors: [message] }.
 * Names in `needed` all resolve in the set.
 */
export function collectNames(set, srcDir) {
  const needed = new Set()
  const errors = []
  for (const file of walk(srcDir)) {
    const text = readFileSync(file, 'utf8')
    const rel = relative(srcDir, file).split(sep).join('/')
    const ranges = iconListRanges(text)
    for (const { value, start } of scanLiterals(text)) {
      const required = isIconPosition(text, start, ranges)
      const where = () => `${rel}:${lineOf(text, start)}`
      let name = null
      const prefixed = PREFIXED.exec(value)
      if (prefixed) {
        const [, prefix, bare] = prefixed
        if (prefix === BUNDLED_SET) {
          if (!resolveChain(set, bare)) errors.push(`${where()}: '${value}' is not in the lucide set`)
          else needed.add(bare)
          continue
        }
        if (required || FOREIGN_SETS.has(prefix)) {
          errors.push(`${where()}: '${value}' uses the ${prefix} icon set; only lucide is bundled`)
        }
        continue
      }
      if (KEBAB.test(value)) name = value
      else if (PASCAL.test(value)) name = toSlug(value)
      else {
        if (required && value !== '') errors.push(`${where()}: '${value}' in an icon position is not an icon name`)
        continue
      }
      if (resolveChain(set, name)) needed.add(name)
      else if (required) {
        const shown = name === value ? `'${value}'` : `'${value}' (${name})`
        errors.push(`${where()}: ${shown} is not in the lucide set`)
      }
    }
  }
  return { needed, errors }
}

function sortDeep(value) {
  if (Array.isArray(value)) return value.map(sortDeep)
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, sortDeep(value[key])]),
    )
  }
  return value
}

/** Builds the sorted IconifyJSON subset for the given names. */
export function buildSubset(set, info, names) {
  const icons = {}
  const aliases = {}
  for (const name of names) {
    for (const step of resolveChain(set, name)) {
      if (step.kind === 'icon') icons[step.name] = set.icons[step.name]
      else aliases[step.name] = set.aliases[step.name]
    }
  }
  const subset = { prefix: set.prefix, info, icons, aliases }
  if (set.width !== undefined) subset.width = set.width
  if (set.height !== undefined) subset.height = set.height
  return sortDeep(subset)
}

function loadSet(source) {
  const hint = 'run npm install in frontend/ (the generator reads the @iconify-json/lucide dev dependency)'
  if (!existsSync(source)) throw new SetupError(`icon set not found: ${source}; ${hint}`)
  const set = JSON.parse(readFileSync(source, 'utf8'))
  if (set.prefix !== BUNDLED_SET || !set.icons) throw new SetupError(`${source} is not the lucide icon set`)
  let info = set.info
  const infoFile = join(dirname(source), 'info.json')
  if (!info && existsSync(infoFile)) info = JSON.parse(readFileSync(infoFile, 'utf8'))
  if (!info?.license) throw new SetupError(`lucide license info not found next to ${source}; ${hint}`)
  const keep = { name: info.name, author: info.author, license: info.license }
  return { set, info: keep }
}

const serialize = (subset) => JSON.stringify(subset, null, 2) + '\n'
const namesOf = (subset) => new Set([...Object.keys(subset.icons ?? {}), ...Object.keys(subset.aliases ?? {})])

function parseArgs(argv) {
  const options = { ...DEFAULTS, check: false }
  const valueFor = { '--source': 'source', '--src-dir': 'srcDir', '--out': 'out' }
  for (let k = 0; k < argv.length; k++) {
    const arg = argv[k]
    if (arg === '--check') options.check = true
    else if (valueFor[arg]) {
      const value = argv[++k]
      if (!value) throw new UsageError(`${arg} needs a value`)
      options[valueFor[arg]] = resolve(value)
    } else throw new UsageError(`unknown argument: ${arg}`)
  }
  return options
}

function main(argv) {
  const options = parseArgs(argv)
  const { set, info } = loadSet(options.source)
  if (!existsSync(options.srcDir)) throw new SetupError(`src dir not found: ${options.srcDir}`)

  const { needed, errors } = collectNames(set, options.srcDir)
  if (errors.length) {
    process.stderr.write(`icon names that do not resolve offline:\n${errors.map((e) => `  ${e}`).join('\n')}\n`)
    return 1
  }
  const fresh = buildSubset(set, info, [...needed].sort())
  const outRel = relative(REPO, options.out).split(sep).join('/')

  if (!options.check) {
    mkdirSync(dirname(options.out), { recursive: true })
    writeFileSync(options.out, serialize(fresh))
    process.stdout.write(
      `wrote ${outRel}: ${Object.keys(fresh.icons).length} icons, ${Object.keys(fresh.aliases).length} aliases\n`,
    )
    return 0
  }

  if (!existsSync(options.out)) {
    process.stderr.write(`${outRel} is missing; run node scripts/build-icon-subset.mjs\n`)
    return 1
  }
  const committed = JSON.parse(readFileSync(options.out, 'utf8'))
  if (JSON.stringify(sortDeep(committed)) === JSON.stringify(fresh)) {
    process.stdout.write(`${outRel} is current (${Object.keys(fresh.icons).length} icons)\n`)
    return 0
  }
  const want = namesOf(fresh)
  const have = namesOf(committed)
  const missing = [...want].filter((name) => !have.has(name)).sort()
  const extra = [...have].filter((name) => !want.has(name)).sort()
  const lines = [`${outRel} is stale; run node scripts/build-icon-subset.mjs`]
  if (missing.length) lines.push(`  missing: ${missing.join(', ')}`)
  if (extra.length) lines.push(`  extra: ${extra.join(', ')}`)
  if (!missing.length && !extra.length) lines.push('  same names, different content (icon data or metadata)')
  process.stderr.write(lines.join('\n') + '\n')
  return 1
}

const invokedDirectly = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (invokedDirectly) {
  try {
    process.exitCode = main(process.argv.slice(2))
  } catch (error) {
    const expected = error instanceof UsageError || error instanceof SetupError
    process.stderr.write(`build-icon-subset: ${expected ? error.message : error.stack}\n`)
    if (error instanceof UsageError) {
      process.stderr.write('usage: node scripts/build-icon-subset.mjs [--source FILE] [--src-dir DIR] [--out FILE] [--check]\n')
    }
    process.exitCode = 2
  }
}
