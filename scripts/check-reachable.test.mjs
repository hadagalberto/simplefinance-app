// Tests for scripts/check-reachable.mjs — run with: node --test scripts/check-reachable.test.mjs
//
// Every case builds its own fixture src tree under the OS temp dir and points the
// checker at it with --src-dir (and --entry where needed). The real frontend tree
// is never read by these tests.

import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = join(dirname(fileURLToPath(import.meta.url)), 'check-reachable.mjs')

let root

/** Creates <root>/<name>/src from a { 'rel/path': content } spec and returns the src dir. */
function fixture(name, files) {
  const srcDir = join(root, name, 'src')
  mkdirSync(srcDir, { recursive: true })
  for (const [rel, content] of Object.entries(files)) {
    const full = join(srcDir, ...rel.split('/'))
    mkdirSync(dirname(full), { recursive: true })
    writeFileSync(full, content)
  }
  return srcDir
}

function run(srcDir, ...extra) {
  return spawnSync(process.execPath, [scriptPath, '--src-dir', srcDir, ...extra], {
    encoding: 'utf8',
  })
}

function runJson(srcDir, ...extra) {
  const res = run(srcDir, '--json', ...extra)
  let report = null
  try {
    report = JSON.parse(res.stdout)
  } catch {
    report = null
  }
  return { res, report }
}

// A small app graph that exercises every specifier form the walker follows.
const GRAPH = {
  'main.tsx': [
    "import { lazy } from 'react'",
    "import { a } from '@/a'",
    "import B from './b'",
    "import '@/styles/app.scss'",
    "export * from './d-mod'",
    "const C = lazy(() => import('@/views/c'))",
    'export { a, B, C }',
    '',
  ].join('\n'),
  'a.ts': "export const a = 'alias import resolved through the @ prefix'\n",
  'b/index.tsx': 'export default function B() { return null }\n',
  'styles/app.scss': '@import "bootstrap/scss/functions";\n@import "part";\n',
  'styles/_part.scss': '.part { color: red; }\n',
  'views/c.tsx': 'export default function C() { return null }\n',
  'd-mod.ts': "export const d = 're-exported module'\n",
}

before(() => {
  root = mkdtempSync(join(tmpdir(), 'sf-reachable-'))
})

after(() => {
  rmSync(root, { recursive: true, force: true })
})

test('alias, relative index, scss partial, lazy import and re-export are all reachable', () => {
  const src = fixture('graph', GRAPH)
  const { res, report } = runJson(src)
  assert.equal(res.status, 0, res.stderr)
  assert.ok(report, 'stdout is JSON')
  assert.equal(report.ok, true)
  assert.deepEqual(report.unreachable, [])
  assert.deepEqual(report.unresolved, [])
  assert.equal(report.reachableCount, Object.keys(GRAPH).length)
})

test('orphan source and orphan image are listed as unreachable, sorted, exit 1', () => {
  const src = fixture('orphans', {
    ...GRAPH,
    'orphan.tsx': 'export const orphan = 1\n',
    'assets/x.png': Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x00, 0x01]),
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 1)
  assert.equal(report.ok, false)
  assert.deepEqual(report.unreachable, ['assets/x.png', 'orphan.tsx'])
})

test('import.meta.glob marks matching JSON reachable and leaves non-matching JSON unreachable', () => {
  const src = fixture('glob', {
    'main.tsx': "import './i18n/index'\n",
    'i18n/index.ts': [
      "const mods = import.meta.glob('./locales/*/*.json', { eager: true }) as Record<string, unknown>",
      '/**',
      ' * A block comment after the glob call must not swallow the code below.',
      ' */',
      "import { extra } from './extra'",
      'export { mods, extra }',
      '',
    ].join('\n'),
    'i18n/extra.ts': 'export const extra = 1\n',
    'i18n/locales/en/common.json': '{"a":1}\n',
    'i18n/locales/pt-BR/common.json': '{"a":1}\n',
    'i18n/locales/stray.json': '{"a":1}\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 1)
  assert.deepEqual(report.unreachable, ['i18n/locales/stray.json'])
  assert.deepEqual(report.unresolved, [])
})

test('an imported image asset is reachable', () => {
  const src = fixture('asset', {
    'main.tsx': "import logo from '@/assets/logo.svg'\nexport default logo\n",
    'assets/logo.svg': '<svg xmlns="http://www.w3.org/2000/svg"></svg>\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 0, res.stderr)
  assert.deepEqual(report.unreachable, [])
})

test('bare package specifiers are ignored and never reported', () => {
  const src = fixture('packages', {
    'main.tsx': [
      "import React from 'react'",
      "import { useQuery } from '@tanstack/react-query'",
      "import 'bootstrap/dist/css/bootstrap.min.css'",
      "import './app.scss'",
      'export { React, useQuery }',
      '',
    ].join('\n'),
    'app.scss': '@import "bootstrap/scss/functions";\n@use "sass:math";\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 0, res.stderr)
  assert.deepEqual(report.unresolved, [])
  assert.deepEqual(report.unreachable, [])
})

test('an unresolved local specifier is reported with its importer, exit 1', () => {
  const src = fixture('unresolved', {
    'main.tsx': "import x from '@/missing'\nimport y from './gone/away'\nexport { x, y }\n",
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 1)
  assert.deepEqual(report.unresolved, [
    { from: 'main.tsx', specifier: './gone/away' },
    { from: 'main.tsx', specifier: '@/missing' },
  ])
})

test('ambient .d.ts files count as reachable', () => {
  const src = fixture('dts', {
    'main.tsx': 'export const x = 1\n',
    'vite-env.d.ts': '/// <reference types="vite/client" />\n',
    'types/global.d.ts': 'declare const g: number\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 0, res.stderr)
  assert.deepEqual(report.unreachable, [])
})

test('scss @use/@forward, comma lists and nested partials resolve locally', () => {
  const src = fixture('scss', {
    'main.tsx': "import './styles/app.scss'\n",
    'styles/app.scss': [
      '// @import "commented-out";',
      '@use "tokens";',
      '@forward "mixins";',
      '@import "components/buttons", "components/cards";',
      '',
    ].join('\n'),
    'styles/_tokens.scss': ':root { --x: 1; }\n',
    'styles/_mixins.scss': '@mixin m { color: red; }\n',
    'styles/components/_buttons.scss': '.btn { color: red; }\n',
    'styles/components/_cards.scss': '.card { color: red; }\n',
    'styles/_commented-out.scss': '.dead { color: red; }\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 1)
  assert.deepEqual(report.unreachable, ['styles/_commented-out.scss'])
  assert.deepEqual(report.unresolved, [])
})

test('a commented-out import does not make its target reachable', () => {
  const src = fixture('comments', {
    'main.tsx': "// import { dead } from './dead'\n/* import './dead2' */\nexport const x = 1\n",
    'dead.ts': 'export const dead = 1\n',
    'dead2.ts': 'export const dead2 = 1\n',
  })
  const { res, report } = runJson(src)
  assert.equal(res.status, 1)
  assert.deepEqual(report.unreachable, ['dead.ts', 'dead2.ts'])
})

test('a missing entry file exits 2 with a message on stderr', () => {
  const src = fixture('noentry', { 'other.ts': 'export const o = 1\n' })
  const res = run(src)
  assert.equal(res.status, 2)
  assert.match(res.stderr, /entry/i)
})

test('a missing src dir exits 2 with a message on stderr', () => {
  const res = run(join(root, 'does-not-exist', 'src'))
  assert.equal(res.status, 2)
  assert.match(res.stderr, /src dir/i)
})

test('--entry overrides the default main.tsx entry', () => {
  const src = fixture('custom-entry', {
    'boot.ts': "import './x'\n",
    'x.ts': 'export const x = 1\n',
  })
  const { res, report } = runJson(src, '--entry', join(src, 'boot.ts'))
  assert.equal(res.status, 0, res.stderr)
  assert.equal(report.entry, 'boot.ts')
  assert.deepEqual(report.unreachable, [])
})

test('an unknown argument exits 2', () => {
  const src = fixture('badarg', GRAPH)
  const res = run(src, '--nope')
  assert.equal(res.status, 2)
})
