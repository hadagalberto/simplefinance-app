// Tests for scripts/check-theme-free.mjs — run with: node --test scripts/check-theme-free.test.mjs
//
// Every case builds its own fixture tree under the OS temp dir and points the
// verifier at it with --themes-dir / --frontend-dir. The real repository kits
// are never read.

import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = join(dirname(fileURLToPath(import.meta.url)), 'check-theme-free.mjs')

// 64+ bytes after trim, so they are compared rather than ignored as trivial.
const CONTENT_X = 'export const sharedValue = "identical content used by the kit and the app";\n'
const CONTENT_Y = 'export const kitOnly = "this file only exists inside the JS kit fixture tree";\n'
const CONTENT_OWN = 'export const ownCode = "authored for the app, never present in any kit at all";\n'
const TINY = 'const a=1\n' // 10 bytes

let root

function put(base, rel, content) {
  const full = join(base, ...rel.split('/'))
  mkdirSync(dirname(full), { recursive: true })
  writeFileSync(full, content)
}

/** Creates <root>/<name>/{themes/TS,themes/JS,frontend} from a spec. */
function fixture(name, { ts = {}, js = {}, fe = {}, emptyDirs = [] }) {
  const base = join(root, name)
  for (const [rel, content] of Object.entries(ts)) put(join(base, 'themes', 'TS'), rel, content)
  for (const [rel, content] of Object.entries(js)) put(join(base, 'themes', 'JS'), rel, content)
  for (const [rel, content] of Object.entries(fe)) put(join(base, 'frontend'), rel, content)
  for (const rel of emptyDirs) mkdirSync(join(base, ...rel.split('/')), { recursive: true })
  return { themes: join(base, 'themes'), frontend: join(base, 'frontend') }
}

function run(dirs, ...extra) {
  return spawnSync(
    process.execPath,
    [scriptPath, '--themes-dir', dirs.themes, '--frontend-dir', dirs.frontend, ...extra],
    { encoding: 'utf8' },
  )
}

function runJson(dirs) {
  const res = run(dirs, '--json')
  return { res, report: JSON.parse(res.stdout) }
}

let identicalDirs
let trivialDirs
let stringDirs
let cleanDirs

before(() => {
  root = mkdtempSync(join(tmpdir(), 'sf-theme-free-'))

  identicalDirs = fixture('identical', {
    ts: { 'src/deep/a.ts': CONTENT_X },
    js: { 'src/b.js': CONTENT_Y },
    fe: {
      'src/other/name.ts': CONTENT_X,
      'node_modules/p/a.ts': CONTENT_X,
      'dist/a.ts': CONTENT_X,
      '.vite/a.ts': CONTENT_X,
      'src/node_modules/a.ts': CONTENT_X,
      'src/own.ts': CONTENT_OWN,
    },
  })

  trivialDirs = fixture('trivial', {
    ts: { 'src/tiny.ts': TINY, 'src/big.ts': CONTENT_Y },
    js: { 'src/b.js': CONTENT_Y },
    fe: { '.gitkeep': '', 'src/tiny.ts': TINY, 'src/own.ts': CONTENT_OWN },
  })

  stringDirs = fixture('strings', {
    ts: { 'src/k.ts': CONTENT_Y },
    js: { 'src/b.js': CONTENT_Y },
    fe: {
      'src/x.ts': '// line one\n// line two\n// Made by CoderThemes\nexport {}\n',
      'src/case.ts': 'UBOLD\nubold\nUbold\n',
      'src/blob.bin': Buffer.concat([Buffer.from('header'), Buffer.from([0]), Buffer.from(' ubold inside binary')]),
    },
  })

  cleanDirs = fixture('clean', {
    ts: { 'src/k.ts': CONTENT_X },
    js: { 'src/b.js': CONTENT_Y },
    fe: { 'src/own.ts': CONTENT_OWN },
  })
})

after(() => {
  rmSync(root, { recursive: true, force: true })
})

test('identical content is found regardless of path and exits 1', () => {
  const { res, report } = runJson(identicalDirs)
  assert.equal(res.status, 1)
  assert.equal(report.ok, false)
  const hit = report.identical.files.find((f) => f.file === 'src/other/name.ts')
  assert.ok(hit, 'src/other/name.ts should be reported as identical')
  assert.ok(hit.kitMatches.includes('TS/src/deep/a.ts'))
  assert.equal(report.identical.byGroup['src/other'], 1)
})

test('node_modules, dist and .vite are excluded at any depth', () => {
  const { report } = runJson(identicalDirs)
  const files = report.identical.files.map((f) => f.file)
  assert.deepEqual(files, ['src/other/name.ts'])
  assert.equal(report.scanned, 2)
})

test('trivial files are listed as ignored and never compared', () => {
  const { res, report } = runJson(trivialDirs)
  assert.deepEqual(report.ignoredTrivial.files, ['.gitkeep', 'src/tiny.ts'])
  assert.equal(report.ignoredTrivial.count, 2)
  assert.equal(report.identical.count, 0)
  assert.equal(res.status, 0)
})

test('ubold|coderthemes string hits are reported with file and line', () => {
  const { res, report } = runJson(stringDirs)
  assert.equal(res.status, 1)
  const hit = report.stringHits.hits.find((h) => h.file === 'src/x.ts')
  assert.ok(hit, 'src/x.ts should have a string hit')
  assert.equal(hit.line, 3)
  assert.equal(hit.match, 'CoderThemes')
  assert.equal(hit.text, '// Made by CoderThemes')
})

test('string matching is case-insensitive', () => {
  const { report } = runJson(stringDirs)
  const matches = report.stringHits.hits.filter((h) => h.file === 'src/case.ts').map((h) => [h.line, h.match])
  assert.deepEqual(matches, [
    [1, 'UBOLD'],
    [2, 'ubold'],
    [3, 'Ubold'],
  ])
  assert.equal(report.stringHits.count, 4)
  assert.equal(report.stringHits.fileCount, 2)
})

test('binary files are not scanned for strings', () => {
  const { report } = runJson(stringDirs)
  assert.equal(report.stringHits.hits.filter((h) => h.file === 'src/blob.bin').length, 0)
})

test('clean tree exits 0 with ok true and Result: PASS', () => {
  const { res, report } = runJson(cleanDirs)
  assert.equal(res.status, 0)
  assert.equal(report.ok, true)
  assert.equal(report.stringHits.count, 0)
  const human = run(cleanDirs)
  assert.equal(human.status, 0)
  assert.match(human.stdout, /^Result: PASS$/m)
})

test('missing JS kit exits 2 with a missing-or-empty message and no stdout', () => {
  const dirs = fixture('no-js', { ts: { 'src/k.ts': CONTENT_X }, fe: { 'src/own.ts': CONTENT_OWN } })
  const res = run(dirs, '--json')
  assert.equal(res.status, 2)
  assert.match(res.stderr, /missing or empty/)
  assert.equal(res.stdout, '')
})

test('empty JS kit exits 2', () => {
  const dirs = fixture('empty-js', {
    ts: { 'src/k.ts': CONTENT_X },
    fe: { 'src/own.ts': CONTENT_OWN },
    emptyDirs: ['themes/JS'],
  })
  const res = run(dirs)
  assert.equal(res.status, 2)
  assert.match(res.stderr, /missing or empty/)
})

test('missing frontend dir exits 2', () => {
  const dirs = fixture('no-frontend', { ts: { 'src/k.ts': CONTENT_X }, js: { 'src/b.js': CONTENT_Y } })
  const res = run(dirs)
  assert.equal(res.status, 2)
  assert.match(res.stderr, /missing or empty/)
  assert.equal(res.stdout, '')
})

test('human report lists sections, groups and the result line', () => {
  const res = run(identicalDirs)
  assert.equal(res.status, 1)
  const out = res.stdout
  assert.match(out, /^Identical files: 1$/m)
  assert.match(out, /^ {2}src\/other \(1\)$/m)
  assert.match(out, /src\/other\/name\.ts {2}= {2}TS\/src\/deep\/a\.ts/)
  assert.match(out, /^Ignored \(trivial, <64 bytes\): 0$/m)
  assert.match(out, /^String hits \(ubold\|coderthemes\): 0 in 0 files$/m)
  assert.match(out, /^Result: FAIL$/m)
})
