// Tests for scripts/build-icon-subset.mjs — run with: node --test scripts/build-icon-subset.test.mjs
//
// Every case builds a tiny icon set (icons.json + info.json, the same two-file
// layout as the @iconify-json package) and a small src tree in a temp dir, then
// runs the generator as a child process and checks its exit code, its stderr
// and the written subset.

import test from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { CATEGORY_ICON_NAMES, toIconifySlug } from '../frontend/src/features/categories/icons.ts'

const here = dirname(fileURLToPath(import.meta.url))
const SCRIPT = resolve(here, 'build-icon-subset.mjs')

const svg = (tag) => ({ body: `<path d="M${tag}"/>` })

const SOURCE = {
  prefix: 'lucide',
  lastModified: 1700000000,
  width: 24,
  height: 24,
  icons: {
    'arrow-left-right': svg('alr'),
    'building-2': svg('b2'),
    check: svg('check'),
    'ellipsis-vertical': svg('ev'),
    'heart-pulse': svg('hp'),
    house: svg('house'),
    plus: svg('plus'),
    tag: svg('tag'),
    unused: svg('unused'),
    x: svg('x'),
  },
  aliases: {
    home: { parent: 'house' },
    'more-vertical': { parent: 'ellipsis-vertical' },
    'dots-vertical': { parent: 'more-vertical' },
  },
}

const INFO = {
  prefix: 'lucide',
  name: 'Lucide',
  total: 10,
  author: { name: 'Lucide Contributors', url: 'https://github.com/lucide-icons/lucide' },
  license: { title: 'ISC', spdx: 'ISC', url: 'https://github.com/lucide-icons/lucide/blob/main/LICENSE' },
  samples: ['check'],
}

/** Creates a temp workspace with the icon set and the given src files. */
function workspace(files, { withSource = true } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'icon-subset-'))
  const pkg = join(root, 'pkg')
  mkdirSync(pkg, { recursive: true })
  if (withSource) {
    writeFileSync(join(pkg, 'icons.json'), JSON.stringify(SOURCE))
    writeFileSync(join(pkg, 'info.json'), JSON.stringify(INFO))
  }
  const src = join(root, 'src')
  for (const [rel, content] of Object.entries(files)) {
    const full = join(src, rel)
    mkdirSync(dirname(full), { recursive: true })
    writeFileSync(full, content)
  }
  mkdirSync(src, { recursive: true })
  const out = join(root, 'out', 'subset.json')
  return {
    root,
    out,
    run: (...extra) =>
      spawnSync(process.execPath, [SCRIPT, '--source', join(pkg, 'icons.json'), '--src-dir', src, '--out', out, ...extra], {
        encoding: 'utf8',
      }),
    read: () => JSON.parse(readFileSync(out, 'utf8')),
    cleanup: () => rmSync(root, { recursive: true, force: true }),
  }
}

function withWorkspace(files, fn, options) {
  const ws = workspace(files, options)
  try {
    fn(ws)
  } finally {
    ws.cleanup()
  }
}

test('collects lucide: literals, resolving bare kebab names and PascalCase names through the slug rule', () => {
  const files = {
    'a.tsx': [
      `const a = 'lucide:check'`,
      `const b = "plus"`,
      'const c = `x`',
      `const d = ['HeartPulse', 'Building2', 'ArrowLeftRight']`,
      `const e = 'primary-button'`, // kebab, not an icon, not in an icon position: ignored
      `const f = 'Exceeded'`, // PascalCase, not an icon: ignored
    ].join('\n'),
  }
  withWorkspace(files, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 0, r.stderr)
    const j = ws.read()
    assert.deepEqual(Object.keys(j.icons), ['arrow-left-right', 'building-2', 'check', 'heart-pulse', 'plus', 'x'])
    assert.deepEqual(j.aliases ?? {}, {})
  })
})

test('a literal in a JSX icon="..." attribute must resolve (exit 1 naming file and name)', () => {
  withWorkspace({ 'feature/Page.tsx': `export const P = () => <Icon icon="nope-icon" />\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /Page\.tsx/)
    assert.match(r.stderr, /nope-icon/)
  })
})

test('a literal in an icon: property must resolve', () => {
  withWorkspace({ 'menu.ts': `export const M = [{ id: 'a', icon: 'gone-away' }]\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /menu\.ts/)
    assert.match(r.stderr, /gone-away/)
  })
})

test('both branches of a conditional directly inside icon={...} must resolve', () => {
  withWorkspace({ 'Toggle.tsx': `export const T = ({ open }) => <Icon icon={open ? 'check' : 'missing-branch'} />\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /missing-branch/)
    assert.doesNotMatch(r.stderr, /'check'/)
  })
})

test('a comparison operand inside icon={...} is not an icon position; a fallback is', () => {
  withWorkspace({ 'Chip.tsx': `export const C = ({ s }) => <Icon icon={s === 'confirmed' ? 'check' : 'x'} />\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 0, r.stderr)
    assert.deepEqual(Object.keys(ws.read().icons), ['check', 'x'])
  })
  withWorkspace({ 'Chip.tsx': `export const C = ({ n }) => <Icon icon={n ?? 'no-fallback'} />\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /no-fallback/)
  })
})

test('PascalCase names in an icon-name list must resolve through the slug rule', () => {
  withWorkspace({ 'icons.ts': `export const CATEGORY_ICON_NAMES: string[] = [\n  'Tag',\n  'NoSuchThing',\n]\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /icons\.ts/)
    assert.match(r.stderr, /NoSuchThing/)
  })
})

test('aliases: an alias lands in aliases with its parent in icons; chains resolve; direct icons are copied', () => {
  withWorkspace({ 'a.tsx': `<Icon icon="lucide:more-vertical" />\n<Icon icon="dots-vertical" />\nconst h = 'Home'\nconst t = 'lucide:tag'\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 0, r.stderr)
    const j = ws.read()
    assert.deepEqual(j.aliases, {
      'dots-vertical': { parent: 'more-vertical' },
      home: { parent: 'house' },
      'more-vertical': { parent: 'ellipsis-vertical' },
    })
    assert.deepEqual(Object.keys(j.icons), ['ellipsis-vertical', 'house', 'tag'])
    assert.deepEqual(j.icons.tag, SOURCE.icons.tag)
  })
})

test('an unknown lucide: name exits 1 naming the file and the name', () => {
  withWorkspace({ 'b.ts': `const x = 'lucide:does-not-exist'\n` }, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 1)
    assert.match(r.stderr, /b\.ts/)
    assert.match(r.stderr, /does-not-exist/)
  })
})

test('a literal from another icon set exits 1 (only lucide is bundled)', () => {
  for (const literal of ['mdi:chevron-up', 'tabler:home']) {
    withWorkspace({ 'c.tsx': `const x = '${literal}'\n` }, (ws) => {
      const r = ws.run()
      assert.equal(r.status, 1, literal)
      assert.match(r.stderr, /only lucide is bundled/)
      assert.ok(r.stderr.includes(literal), r.stderr)
    })
  }
})

test('i18n keys, comments and dynamic template literals are not icon names', () => {
  const files = {
    'd.tsx': [
      `const a = t('common:retry')`,
      `const b = t('alerts:form.titleNew')`,
      `// an old note: icon="nope-in-comment" and 'lucide:also-nope'`,
      `/* 'mdi:block-comment' */`,
      'const c = `lucide:${name}`',
      `<Icon icon="check" />`,
    ].join('\n'),
  }
  withWorkspace(files, (ws) => {
    const r = ws.run()
    assert.equal(r.status, 0, r.stderr)
    assert.deepEqual(Object.keys(ws.read().icons), ['check'])
  })
})

test('output is deterministic, sorted and carries prefix, info (license), width and height', () => {
  withWorkspace({ 'e.tsx': `<Icon icon="x" />\n<Icon icon="check" />\n<Icon icon="home" />\n` }, (ws) => {
    assert.equal(ws.run().status, 0)
    const first = readFileSync(ws.out, 'utf8')
    assert.equal(ws.run().status, 0)
    assert.equal(readFileSync(ws.out, 'utf8'), first)
    const j = JSON.parse(first)
    assert.equal(j.prefix, 'lucide')
    assert.equal(j.width, 24)
    assert.equal(j.height, 24)
    assert.equal(j.info.name, 'Lucide')
    assert.equal(j.info.license.spdx, 'ISC')
    assert.equal(j.info.author.name, 'Lucide Contributors')
    assert.deepEqual(Object.keys(j), [...Object.keys(j)].sort())
    assert.deepEqual(Object.keys(j.icons), [...Object.keys(j.icons)].sort())
    assert.ok(first.startsWith('{\n  "'), 'two-space indent')
    assert.ok(first.endsWith('}\n'))
  })
})

test('--check exits 0 on a fresh file (also with CRLF line endings) and 1 on a stale one naming missing and extra names', () => {
  withWorkspace({ 'f.tsx': `<Icon icon="x" />\n<Icon icon="check" />\n` }, (ws) => {
    assert.equal(ws.run().status, 0)
    const ok = ws.run('--check')
    assert.equal(ok.status, 0, ok.stderr)

    const text = readFileSync(ws.out, 'utf8')
    writeFileSync(ws.out, text.replace(/\n/g, '\r\n'))
    assert.equal(ws.run('--check').status, 0, 'CRLF checkout is not stale')

    const stale = JSON.parse(text)
    delete stale.icons.check
    stale.icons.plus = SOURCE.icons.plus
    writeFileSync(ws.out, JSON.stringify(stale, null, 2) + '\n')
    const r = ws.run('--check')
    assert.equal(r.status, 1)
    assert.match(r.stderr, /missing.*check/s)
    assert.match(r.stderr, /extra.*plus/s)
    // --check never rewrites the file
    assert.equal(JSON.parse(readFileSync(ws.out, 'utf8')).icons.plus.body, SOURCE.icons.plus.body)
  })
})

test('--check exits 1 when the subset file was never generated', () => {
  withWorkspace({ 'g.tsx': `<Icon icon="x" />\n` }, (ws) => {
    const r = ws.run('--check')
    assert.equal(r.status, 1)
  })
})

test('a missing source icon set exits 2 and says to run npm install in frontend/', () => {
  withWorkspace(
    { 'h.tsx': `<Icon icon="x" />\n` },
    (ws) => {
      const r = ws.run()
      assert.equal(r.status, 2)
      assert.match(r.stderr, /npm install/)
      assert.match(r.stderr, /frontend/)
    },
    { withSource: false },
  )
})

test('every icon the backend category seed stores is in the committed subset', (t) => {
  const seeder = resolve(here, '..', 'src', 'SimpleFinance.Infrastructure', 'Categorization', 'CategorySeeder.cs')
  if (!existsSync(seeder)) return t.skip('backend seed source not present')
  const cs = readFileSync(seeder, 'utf8')
  // Parent rows: new("Name", CategoryType.X, "Icon", ...); children: new ChildSeed("Name", "Icon").
  const parents = [...cs.matchAll(/new\("[^"]*",\s*CategoryType\.\w+,\s*"([A-Za-z0-9]+)"/g)].map((m) => m[1])
  const children = [...cs.matchAll(/new ChildSeed\("[^"]*",\s*"([A-Za-z0-9]+)"\)/g)].map((m) => m[1])
  assert.ok(parents.length >= 5 && children.length >= 5, `seed icons found: ${parents.length} parents, ${children.length} children`)
  const subset = JSON.parse(readFileSync(resolve(here, '..', 'frontend', 'src', 'components', 'icons', 'lucide-subset.json'), 'utf8'))
  const missing = [...new Set([...parents, ...children])].filter((name) => {
    const slug = toIconifySlug(name)
    return !subset.icons[slug] && !subset.aliases?.[slug]
  })
  assert.deepEqual(missing, [], 'seeded category icons missing from lucide-subset.json')
})

test('the app slug rule (toIconifySlug) inserts a hyphen between a letter and a digit', () => {
  assert.equal(toIconifySlug('Building2'), 'building-2')
  assert.equal(toIconifySlug('HeartPulse'), 'heart-pulse')
  assert.equal(toIconifySlug('ArrowLeftRight'), 'arrow-left-right')
  assert.equal(toIconifySlug('Tv'), 'tv')
  for (const name of CATEGORY_ICON_NAMES) assert.match(toIconifySlug(name), /^[a-z0-9]+(-[a-z0-9]+)*$/, name)
})
