// node:test suite for scripts/check-style-contrast.mjs.
//
// Run: node --test scripts/check-style-contrast.test.mjs
//
// Fixtures are tiny CSS files written to a temp dir; the gate runs as a child
// process so exit codes and stdout/stderr are asserted exactly as CI sees them.

import { test, after } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'
import process from 'node:process'

const here = dirname(fileURLToPath(import.meta.url))
const script = resolve(here, 'check-style-contrast.mjs')
const scriptUrl = pathToFileURL(script).href
const root = mkdtempSync(join(tmpdir(), 'sf-contrast-'))
let counter = 0

after(() => {
  rmSync(root, { recursive: true, force: true })
})

// Readable baseline values, authored for the fixtures only.
const LIGHT = {
  '--sf-surface-body': '#f4f4f8',
  '--sf-surface-card': '#ffffff',
  '--sf-surface-raised': '#ffffff',
  '--bs-body-bg': '#f4f4f8',
  '--sf-income': '#0a6e4a',
  '--sf-expense': '#b0263b',
  '--sf-transfer': '#2a55b0',
  '--sf-neutral': '#1a1f2e',
  '--sf-chart-text': '#4f566b',
  '--bs-body-color': '#1a1f2e',
  '--bs-secondary-color': 'rgba(26, 31, 46, .75)',
  '--bs-primary-text-emphasis': '#2c1d66',
  '--bs-secondary-text-emphasis': '#262a34',
  '--bs-success-text-emphasis': '#0a3622',
  '--bs-danger-text-emphasis': '#58151c',
  '--bs-warning-text-emphasis': '#664d03',
  '--bs-info-text-emphasis': '#055160',
  '--sf-chart-primary': '#5b3ae0',
  '--sf-chart-gray': '#6f768a',
  '--sf-chart-1': '#5b3ae0',
  '--sf-chart-2': '#0a7d55',
  '--sf-chart-3': '#a36500',
  '--sf-chart-4': '#1a74ad',
  '--sf-chart-5': '#c2386f',
  '--sf-chart-6': '#55801a',
}

const DARK = {
  '--sf-surface-body': '#0e1322',
  '--sf-surface-card': '#151b2e',
  '--sf-surface-raised': '#1b2238',
  '--bs-body-bg': '#0e1322',
  '--sf-income': '#3ddc97',
  '--sf-expense': '#ff7a8e',
  '--sf-transfer': '#8ab2ff',
  '--sf-neutral': '#e4e7f2',
  '--sf-chart-text': '#aab2ca',
  '--bs-body-color': '#dfe3ef',
  '--bs-secondary-color': 'rgba(223, 227, 239, .75)',
  '--bs-primary-text-emphasis': '#b8a8ff',
  '--bs-secondary-text-emphasis': '#c0c4d2',
  '--bs-success-text-emphasis': '#75b798',
  '--bs-danger-text-emphasis': '#ea868f',
  '--bs-warning-text-emphasis': '#ffda6a',
  '--bs-info-text-emphasis': '#6edff6',
  '--sf-chart-primary': '#a594ff',
  '--sf-chart-gray': '#7a83a0',
  '--sf-chart-1': '#a594ff',
  '--sf-chart-2': '#3ddc97',
  '--sf-chart-3': '#ffb547',
  '--sf-chart-4': '#5cc8ff',
  '--sf-chart-5': '#ff7eb6',
  '--sf-chart-6': '#c3e66e',
}

function block(selector, vars) {
  const body = Object.entries(vars)
    .map(([k, v]) => `${k}: ${v}`)
    .join(';')
  return `${selector}{${body}}`
}

/** Writes a fixture dir; `files` maps file name -> CSS text. */
function fixture(files) {
  const dir = join(root, `case-${++counter}`)
  mkdirSync(dir, { recursive: true })
  for (const [name, text] of Object.entries(files)) writeFileSync(join(dir, name), text)
  return dir
}

function css({ light = LIGHT, dark = DARK } = {}) {
  return `.btn{color:red}${block(':root,[data-bs-theme=light]', light)}${block('[data-bs-theme=dark]', dark)}`
}

function run(dir, ...extra) {
  const r = spawnSync(process.execPath, [script, '--css-dir', dir, ...extra], { encoding: 'utf8' })
  return { code: r.status, stdout: r.stdout ?? '', stderr: r.stderr ?? '' }
}

function without(vars, name) {
  const copy = { ...vars }
  delete copy[name]
  return copy
}

test('contrastRatio: black on white is 21, a color on itself is 1', async () => {
  const { contrastRatio } = await import(scriptUrl)
  assert.equal(Math.round(contrastRatio('#000000', '#ffffff') * 100) / 100, 21)
  assert.equal(Math.round(contrastRatio('#ffffff', '#000000') * 100) / 100, 21)
  assert.equal(contrastRatio('#6d4aff', '#6d4aff'), 1)
})

test('parseColor: short hex, 8-digit hex, uppercase RGBA, space syntax', async () => {
  const { parseColor } = await import(scriptUrl)
  assert.deepEqual(parseColor('#abc'), { r: 170, g: 187, b: 204, a: 1 })
  assert.deepEqual(parseColor('#aabbcc80'), { r: 170, g: 187, b: 204, a: 128 / 255 })
  assert.deepEqual(parseColor('RGBA(10, 20, 30, .5)'), { r: 10, g: 20, b: 30, a: 0.5 })
  assert.deepEqual(parseColor('rgb(10 20 30 / 0.25)'), { r: 10, g: 20, b: 30, a: 0.25 })
  assert.deepEqual(parseColor('rgb(1,2,3)'), { r: 1, g: 2, b: 3, a: 1 })
  assert.equal(parseColor('transparent'), null)
  assert.equal(parseColor('var(--x)'), null)
})

test('readable fixture in both modes passes (exit 0, ends with Result: PASS)', () => {
  const r = run(fixture({ 'a.css': css() }))
  assert.equal(r.code, 0, r.stdout + r.stderr)
  assert.match(r.stdout.trimEnd(), /Result: PASS$/)
  assert.match(r.stdout, /^light {2}--sf-income on --sf-surface-card {2}[\d.]+:1 \(min 4\.5\) {2}PASS$/m)
  assert.match(r.stdout, /^dark {2}--sf-chart-6 on --sf-surface-card {2}[\d.]+:1 \(min 3\) {2}PASS$/m)
})

test('dark --sf-income under 4.5:1 on the dark card fails (exit 1)', () => {
  const dir = fixture({ 'a.css': css({ dark: { ...DARK, '--sf-income': '#2a6b4c' } }) })
  const r = run(dir)
  assert.equal(r.code, 1, r.stdout + r.stderr)
  assert.match(r.stdout, /^dark {2}--sf-income on --sf-surface-card .*FAIL$/m)
  assert.match(r.stdout.trimEnd(), /Result: FAIL$/)
})

test('var() resolves within the same mode and dark inherits un-overridden :root values', () => {
  // Light defines --sf-income through var(); dark omits several tokens entirely
  // and must inherit them from :root (they are readable on the dark card too).
  const light = { ...LIGHT, '--sf-income': 'var(--sf-green, #000)', '--sf-green': '#0a6e4a' }
  const dark = without(without(DARK, '--sf-chart-3'), '--sf-chart-4')
  dark['--sf-income'] = 'var(--sf-green)'
  dark['--sf-green'] = '#3ddc97'
  const r = run(fixture({ 'a.css': css({ light, dark }) }))
  assert.equal(r.code, 0, r.stdout + r.stderr)
  assert.match(r.stdout, /^dark {2}--sf-chart-3 on --sf-surface-card {2}[\d.]+:1/m)

  // A var() pointing at an undefined name with a fallback uses the fallback.
  const fb = { ...LIGHT, '--sf-neutral': 'var(--nope, #1a1f2e)' }
  assert.equal(run(fixture({ 'a.css': css({ light: fb }) })).code, 0)
})

test('alpha foreground is composited over each background', async () => {
  const { contrastRatio } = await import(scriptUrl)
  // 75% white over black is rgb(191,191,191) — not the same as opaque white.
  const composited = contrastRatio('rgba(255, 255, 255, .75)', '#000000')
  const expected = contrastRatio('#bfbfbf', '#000000')
  assert.ok(Math.abs(composited - expected) < 0.05, `${composited} vs ${expected}`)
  assert.ok(composited < contrastRatio('#ffffff', '#000000'))

  // Fully transparent secondary color on the card would read as the card: fail.
  const dark = { ...DARK, '--bs-secondary-color': 'rgba(223, 227, 239, .1)' }
  const r = run(fixture({ 'a.css': css({ dark }) }))
  assert.equal(r.code, 1)
  assert.match(r.stdout, /^dark {2}--bs-secondary-color on --sf-surface-card .*FAIL$/m)
})

test('short hex and uppercase RGBA in the built CSS parse', () => {
  const light = { ...LIGHT, '--sf-surface-card': '#fff', '--bs-secondary-color': 'RGBA(26, 31, 46, .8)' }
  const r = run(fixture({ 'a.css': css({ light }) }))
  assert.equal(r.code, 0, r.stdout + r.stderr)
})

test('required token missing in a mode exits 2 naming it', () => {
  const r = run(fixture({ 'a.css': css({ light: without(LIGHT, '--sf-chart-6'), dark: without(DARK, '--sf-chart-6') }) }))
  assert.equal(r.code, 2)
  assert.match(r.stderr, /--sf-chart-6/)
})

test('unparseable required token exits 2 naming token and mode', () => {
  const r = run(fixture({ 'a.css': css({ dark: { ...DARK, '--sf-expense': 'hsl(350 80% 60%)' } }) }))
  assert.equal(r.code, 2)
  assert.match(r.stderr, /--sf-expense/)
  assert.match(r.stderr, /dark/)
})

test('translucent background is a setup error (exit 2)', () => {
  const r = run(fixture({ 'a.css': css({ dark: { ...DARK, '--sf-surface-card': 'rgba(21, 27, 46, .5)' } }) }))
  assert.equal(r.code, 2)
  assert.match(r.stderr, /--sf-surface-card/)
})

test('empty css dir exits 2 and tells you to build first', () => {
  const dir = fixture({ 'readme.txt': 'not css' })
  const r = run(dir)
  assert.equal(r.code, 2)
  assert.match(r.stderr, /run npm --prefix frontend run build first/)
})

test('--bs-body-bg differing from --sf-surface-body fails (exit 1)', () => {
  const r = run(fixture({ 'a.css': css({ dark: { ...DARK, '--bs-body-bg': '#000000' } }) }))
  assert.equal(r.code, 1, r.stdout + r.stderr)
  assert.match(r.stdout, /^dark {2}--bs-body-bg = --sf-surface-body .*FAIL$/m)
})

test('rules spread over several files and quoted attribute values are read', () => {
  const lightHalf = Object.fromEntries(Object.entries(LIGHT).slice(0, 10))
  const lightRest = Object.fromEntries(Object.entries(LIGHT).slice(10))
  const dir = fixture({
    'a.css': block(':root', lightHalf),
    'b.css': block('[data-bs-theme="light"]', lightRest) + block('[data-bs-theme="dark"]', DARK),
  })
  const r = run(dir)
  assert.equal(r.code, 0, r.stdout + r.stderr)
})

test('--json prints ok and per-mode arrays', () => {
  const r = run(fixture({ 'a.css': css() }), '--json')
  assert.equal(r.code, 0, r.stderr)
  const parsed = JSON.parse(r.stdout)
  assert.equal(parsed.ok, true)
  assert.ok(parsed.modes.light.length > 0)
  assert.ok(parsed.modes.dark.length > 0)
  assert.equal(parsed.modes.light.length, parsed.modes.dark.length)
})

test('CLI entry guard fires when run as a script (stdout ends with a Result line)', () => {
  const r = spawnSync(process.execPath, [script, '--css-dir', fixture({ 'a.css': css() })], {
    encoding: 'utf8',
    cwd: root,
  })
  assert.match((r.stdout ?? '').trimEnd(), /\nResult: (PASS|FAIL)$/)
})
