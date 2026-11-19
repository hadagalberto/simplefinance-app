// Tests for the near-duplicate (similarity) section of scripts/check-theme-free.mjs.
// Run with: node --test scripts/check-theme-similarity.test.mjs
//
// Like check-theme-free.test.mjs, every case builds its own small fixture tree
// under the OS temp dir and points the verifier at it with --themes-dir and
// --frontend-dir. The real kits are never read; all fixture text is synthetic.

import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = join(dirname(fileURLToPath(import.meta.url)), 'check-theme-free.mjs')

let root
let counter = 0

function put(base, rel, content) {
  const full = join(base, ...rel.split('/'))
  mkdirSync(dirname(full), { recursive: true })
  writeFileSync(full, content)
}

/** Creates a fresh <root>/<n>/{themes/TS,themes/JS,frontend} tree. Both kits always get a filler file. */
function fixture({ ts = {}, js = {}, fe = {} }) {
  const base = join(root, `case-${++counter}`)
  const tsFiles = { 'filler/unrelated.ts': fillerFile(), ...ts }
  const jsFiles = { 'filler/unrelated.js': fillerFile(), ...js }
  for (const [rel, content] of Object.entries(tsFiles)) put(join(base, 'themes', 'TS'), rel, content)
  for (const [rel, content] of Object.entries(jsFiles)) put(join(base, 'themes', 'JS'), rel, content)
  for (const [rel, content] of Object.entries(fe)) put(join(base, 'frontend'), rel, content)
  return { themes: join(base, 'themes'), frontend: join(base, 'frontend') }
}

function run(dirs, ...extra) {
  return spawnSync(
    process.execPath,
    [scriptPath, '--themes-dir', dirs.themes, '--frontend-dir', dirs.frontend, ...extra],
    { encoding: 'utf8' },
  )
}

function runJson(dirs, ...extra) {
  const res = run(dirs, '--json', ...extra)
  assert.notEqual(res.stdout, '', `no JSON on stdout (exit ${res.status}): ${res.stderr}`)
  return { res, report: JSON.parse(res.stdout) }
}

/** A distinct code line, always well over the 12-character floor. */
function codeLine(tag, i) {
  return `const ${tag}Value${String(i).padStart(2, '0')} = computeThing("${tag}", ${i});`
}

function codeFile(tag, count) {
  return Array.from({ length: count }, (_, i) => codeLine(tag, i)).join('\n') + '\n'
}

function fillerFile() {
  return codeFile('fillerOnly', 12)
}

/** Copy of codeFile(tag, count) where the listed indexes are replaced by authored lines. */
function editedCopy(tag, count, editIndexes) {
  const edits = new Set(editIndexes)
  return (
    Array.from({ length: count }, (_, i) =>
      edits.has(i) ? `let rewrittenLine${i} = somethingElseEntirely(${i * 7});` : codeLine(tag, i),
    ).join('\n') + '\n'
  )
}

function jsonFile(pairs) {
  const body = pairs.map(([k, v], i) => `  "${k}": "${v}"${i < pairs.length - 1 ? ',' : ''}`)
  return ['{', ...body, '}'].join('\n') + '\n'
}

const JSON_PAIRS = Array.from({ length: 16 }, (_, i) => [
  `option-name-${String(i).padStart(2, '0')}`,
  `value-${String(i).padStart(2, '0')}`,
])

function similarEntry(report, file) {
  return report.similar.files.find((f) => f.file === file)
}

function scoreOf(report, file) {
  const entry = report.similar.top.find((f) => f.file === file) ?? similarEntry(report, file)
  assert.ok(entry, `${file} should be scored`)
  return entry
}

before(() => {
  root = mkdtempSync(join(tmpdir(), 'sf-theme-sim-'))
})

after(() => {
  rmSync(root, { recursive: true, force: true })
})

test('one edited line is not identical but is a near-duplicate (exit 1)', () => {
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: { 'src/ui/Box.tsx': editedCopy('panel', 20, [7]) },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 1)
  assert.equal(report.ok, false)
  assert.equal(report.identical.count, 0)
  assert.equal(report.similar.count, 1)
  const hit = similarEntry(report, 'src/ui/Box.tsx')
  assert.ok(hit, 'src/ui/Box.tsx should be an offender')
  assert.ok(hit.score >= 0.9, `score ${hit.score} should be at least 0.9`)
  assert.equal(hit.kitFile, 'TS/src/widgets/Panel.tsx')
  assert.equal(hit.metric, 'lines')
  assert.equal(hit.units, 20)
  assert.equal(hit.shared, 19)
})

test('every third line edited still fails (line-set, not shingles)', () => {
  const dirs = fixture({
    js: { 'src/widgets/Panel.jsx': codeFile('panel', 20) },
    fe: { 'src/ui/Box.tsx': editedCopy('panel', 20, [2, 5, 8, 11, 14, 17]) },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 1)
  const hit = similarEntry(report, 'src/ui/Box.tsx')
  assert.ok(hit, 'scattered-edit copy should be an offender')
  assert.equal(hit.kitFile, 'JS/src/widgets/Panel.jsx')
  assert.ok(hit.score > 0.6 && hit.score < 0.75, `score ${hit.score} should be about 0.7`)
})

test('authored file sharing a quarter of its lines passes (exit 0)', () => {
  const own = Array.from({ length: 15 }, (_, i) => `export const authored${i} = buildOwnValue(${i}, 'mine');`)
  const shared = Array.from({ length: 5 }, (_, i) => codeLine('panel', i))
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: { 'src/ui/Mine.tsx': [...shared, ...own].join('\n') + '\n' },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 0, res.stdout)
  assert.equal(report.ok, true)
  assert.equal(report.similar.count, 0)
  const entry = scoreOf(report, 'src/ui/Mine.tsx')
  assert.equal(entry.units, 20)
  assert.equal(entry.shared, 5)
  assert.equal(entry.score, 0.25)
})

test('whitespace, comments and import lines do not hide a copy', () => {
  const kit = [
    "import { alpha } from './alpha'",
    "import beta from './beta'",
    '',
    ...Array.from({ length: 10 }, (_, i) => codeLine('norm', i)),
  ].join('\n')
  const lines = Array.from({ length: 10 }, (_, i) => codeLine('norm', i))
  const copy = [
    "import beta from './beta'",
    "import { gamma, delta } from './extra'",
    'import {',
    '  first,',
    '  secondThingLong,',
    "} from './multi-line-module'",
    "import './side-effect.css'",
    "export { thing } from './reexported'",
    "@use 'sass:math';",
    "import { alpha } from './alpha'",
    '// a brand new explanatory comment line here',
    '/* a block comment',
    '   that spans several lines of text */',
    '<!-- a markup comment that is long enough -->',
    `      ${lines[0].replace(/ /g, '   ')}`,
    `${lines[1]}   // trailing remark that should vanish`,
    `${lines[2]} /* inline block */`,
    '',
    '',
    ...lines.slice(3),
  ].join('\n')
  const dirs = fixture({ ts: { 'src/norm.ts': kit }, fe: { 'src/norm-copy.ts': copy } })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 1)
  const hit = similarEntry(report, 'src/norm-copy.ts')
  assert.ok(hit, 'normalized copy should be an offender')
  assert.equal(hit.units, 10, 'only the ten code lines are scored')
  assert.equal(hit.score, 1)
})

test('comment markers inside strings (globs, URLs) do not swallow lines', () => {
  const body = [
    "const sourceGlob = ['**/*.{ts,tsx}'];",
    ...Array.from({ length: 4 }, (_, i) => codeLine('glob', i)),
    "const testGlob = ['e2e/**/*.ts', 'src/**/*.test.ts'];",
    "const homepage = 'https://example.org/docs/page';",
    ...Array.from({ length: 4 }, (_, i) => codeLine('glob', i + 10)),
  ]
  const dirs = fixture({
    ts: { 'src/unrelated.ts': codeFile('other', 12) },
    fe: { 'src/globs.ts': body.join('\n') + '\n' },
  })
  const { report } = runJson(dirs)
  const entry = scoreOf(report, 'src/globs.ts')
  assert.equal(entry.units, 11, 'every line survives normalization')
})

test('an unterminated block opener or a # comment with a glob does not swallow the file', () => {
  const lines = Array.from({ length: 10 }, (_, i) => `location_block_${i} proxy_target_${i};`)
  const conf = ['# forwards /api/* to the backend service', ...lines.slice(0, 5), '<!-- never closed', ...lines.slice(5)]
  const ignore = ['.editor-settings/*', ...Array.from({ length: 9 }, (_, i) => `generated-output-folder-${i}/`)]
  const dirs = fixture({
    ts: { 'src/unrelated.ts': codeFile('other', 12) },
    fe: { 'deploy/site.conf': conf.join('\n') + '\n', '.ignorelist': ignore.join('\n') + '\n' },
  })
  const { report } = runJson(dirs)
  assert.equal(scoreOf(report, 'deploy/site.conf').units, 11, 'ten directives plus the literal opener line')
  assert.equal(scoreOf(report, '.ignorelist').units, 10, 'the glob line and every following line survive')
})

test('lines present in 10 or more kit files are ignored as boilerplate', () => {
  const common = Array.from({ length: 10 }, (_, i) => `return renderSharedWrapper(props, ${i});`)
  const ts = {}
  const js = {}
  for (let k = 0; k < 10; k++) {
    const file = [...common, ...Array.from({ length: 4 }, (_, i) => codeLine(`kit${k}`, i))].join('\n') + '\n'
    if (k < 5) ts[`src/page${k}.tsx`] = file
    else js[`src/page${k}.jsx`] = file
  }
  const mine = [...common, ...Array.from({ length: 10 }, (_, i) => codeLine('mine', i))].join('\n') + '\n'
  const dirs = fixture({ ts, js, fe: { 'src/Mine.tsx': mine } })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 0, res.stdout)
  const entry = scoreOf(report, 'src/Mine.tsx')
  assert.equal(entry.units, 10, 'the ten ubiquitous lines are removed')
  assert.equal(entry.shared, 0)
})

test('a near-copy below 8 scored units is listed as too small and does not fail', () => {
  const dirs = fixture({
    ts: { 'src/tiny.ts': codeFile('tiny', 6) },
    fe: { 'src/tiny-copy.ts': codeFile('tiny', 6) + 'const extraLocalLine = 42;\n' },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 0, res.stdout)
  assert.equal(report.similar.count, 0)
  const small = report.similar.tooSmall.files.find((f) => f.file === 'src/tiny-copy.ts')
  assert.ok(small, 'src/tiny-copy.ts should be listed as too small')
  assert.equal(small.units, 7)
  assert.equal(report.similar.tooSmall.count, report.similar.tooSmall.files.length)
  assert.equal(report.similar.top.some((f) => f.file === 'src/tiny-copy.ts'), false)
})

test('JSON uses ordered shingles: reordered keys pass, a copy with one edited value fails', () => {
  const reordered = [...JSON_PAIRS].reverse()
  const edited = JSON_PAIRS.map(([k, v], i) => (i === 8 ? [k, 'a-different-value'] : [k, v]))
  const dirs = fixture({
    ts: { 'config/settings.json': jsonFile(JSON_PAIRS) },
    fe: { 'reordered.json': jsonFile(reordered), 'edited.json': jsonFile(edited) },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 1)
  assert.equal(report.identical.count, 0)
  assert.deepEqual(
    report.similar.files.map((f) => f.file),
    ['edited.json'],
  )
  const hit = similarEntry(report, 'edited.json')
  assert.equal(hit.metric, 'shingles5')
  assert.equal(hit.kitFile, 'TS/config/settings.json')
  assert.equal(hit.units, 12)
  assert.ok(hit.score >= 0.4 && hit.score < 1, `score ${hit.score}`)
  const reorderedEntry = scoreOf(report, 'reordered.json')
  assert.equal(reorderedEntry.metric, 'shingles5')
  assert.ok(reorderedEntry.score < 0.4, `reordered score ${reorderedEntry.score}`)
})

test('lockfiles are excluded from scoring and listed', () => {
  const lock = jsonFile(JSON_PAIRS)
  const dirs = fixture({
    ts: { 'package-lock.json': lock, 'yarn.lock': codeFile('lock', 20) },
    fe: {
      'package-lock.json': jsonFile(JSON_PAIRS.map(([k, v], i) => (i === 3 ? [k, 'changed'] : [k, v]))),
      'npm-shrinkwrap.json': jsonFile(JSON_PAIRS.map(([k, v], i) => (i === 4 ? [k, 'changed'] : [k, v]))),
      'yarn.lock': editedCopy('lock', 20, [3]),
      'sub/pnpm-lock.yaml': editedCopy('lock', 20, [5]),
    },
  })
  const { res, report } = runJson(dirs)
  assert.equal(res.status, 0, res.stdout)
  assert.deepEqual(report.similar.excluded, [
    'npm-shrinkwrap.json',
    'package-lock.json',
    'sub/pnpm-lock.yaml',
    'yarn.lock',
  ])
  assert.equal(report.similar.top.length, 0)
  assert.equal(report.similar.tooSmall.files.some((f) => report.similar.excluded.includes(f.file)), false)
})

test('report shape: threshold, minUnits, metric, top sorted descending', () => {
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: {
      'src/a.tsx': editedCopy('panel', 20, [1]),
      'src/b.tsx': editedCopy('panel', 20, [1, 4, 7, 10, 13, 16, 19]),
      'src/c.tsx': codeFile('authored', 10),
    },
  })
  const { report } = runJson(dirs)
  const s = report.similar
  assert.equal(s.threshold, 0.4)
  assert.equal(s.minUnits, 8)
  assert.deepEqual(s.metric, { default: 'lines', json: 'shingles5' })
  assert.equal(s.count, 2)
  assert.deepEqual(
    s.top.map((f) => f.file),
    ['src/a.tsx', 'src/b.tsx', 'src/c.tsx'],
  )
  for (let i = 1; i < s.top.length; i++) assert.ok(s.top[i - 1].score >= s.top[i].score)
  assert.ok(s.top.length <= 10)
})

test('human report has a near-duplicates section with scores', () => {
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: { 'src/ui/Box.tsx': editedCopy('panel', 20, [7]) },
  })
  const res = run(dirs)
  assert.equal(res.status, 1)
  assert.match(res.stdout, /^Near-duplicates \(>= 40%\): 1$/m)
  assert.match(res.stdout, /src\/ui\/Box\.tsx +95% +19\/20 +TS\/src\/widgets\/Panel\.tsx/)
  assert.match(res.stdout, /^Top similarity scores:/m)
  assert.match(res.stdout, /^Result: FAIL$/m)
})

test('--similarity-threshold 0.9 lets the scattered-edit copy pass', () => {
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: { 'src/ui/Box.tsx': editedCopy('panel', 20, [2, 5, 8, 11, 14, 17]) },
  })
  const { res, report } = runJson(dirs, '--similarity-threshold', '0.9')
  assert.equal(res.status, 0, res.stdout)
  assert.equal(report.similar.threshold, 0.9)
  assert.equal(report.similar.count, 0)
  assert.match(run(dirs, '--similarity-threshold', '0.9').stdout, /^Near-duplicates \(>= 90%\): 0$/m)
})

test('an invalid or missing --similarity-threshold exits 2 with a usage message', () => {
  const dirs = fixture({
    ts: { 'src/widgets/Panel.tsx': codeFile('panel', 20) },
    fe: { 'src/own.ts': codeFile('own', 10) },
  })
  for (const value of [['0'], ['1.5'], ['-0.2'], ['abc'], []]) {
    const res = run(dirs, '--similarity-threshold', ...value)
    assert.equal(res.status, 2, `value ${JSON.stringify(value)}`)
    assert.match(res.stderr, /usage/i)
    assert.equal(res.stdout, '')
  }
  assert.equal(run(dirs, '--similarity-threshold', '1').status, 0)
})

test('a missing kit still exits 2 with the similarity check in place', () => {
  const base = join(root, 'no-ts-kit')
  put(join(base, 'themes', 'JS'), 'src/a.js', codeFile('panel', 20))
  put(join(base, 'frontend'), 'src/a.tsx', editedCopy('panel', 20, [3]))
  const res = run({ themes: join(base, 'themes'), frontend: join(base, 'frontend') }, '--json')
  assert.equal(res.status, 2)
  assert.match(res.stderr, /missing or empty/)
  assert.equal(res.stdout, '')
})
