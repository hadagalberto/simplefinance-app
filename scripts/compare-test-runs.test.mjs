// Tests for scripts/compare-test-runs.mjs — run with: node --test scripts/compare-test-runs.test.mjs
//
// Each case writes two small TRX files to a temp dir and runs the CLI on them.

import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = join(dirname(fileURLToPath(import.meta.url)), 'compare-test-runs.mjs')
const KNOWN = 'SimpleFinance.UnitTests.Sync.ConcurrentSyncTests.SerializedNonOverlappingSyncLogWindows'

let root
let counter = 0

before(() => {
  root = mkdtempSync(join(tmpdir(), 'sf-compare-trx-'))
})

after(() => {
  rmSync(root, { recursive: true, force: true })
})

/** Builds a TRX document. Attribute order alternates to prove the parser does not depend on it. */
function trx(results) {
  const rows = results.map(([name, outcome], i) =>
    i % 2 === 0
      ? `    <UnitTestResult executionId="e${i}" testId="t${i}" testName="${name}" computerName="pc" duration="00:00:00.01" outcome="${outcome}" testListId="l" />`
      : `    <UnitTestResult outcome="${outcome}" executionId="e${i}" testName="${name}" testId="t${i}">\n      <Output><StdOut>log</StdOut></Output>\n    </UnitTestResult>`,
  )
  return [
    '<?xml version="1.0" encoding="utf-8"?>',
    '<TestRun id="r" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">',
    '  <Results>',
    ...rows,
    '  </Results>',
    '</TestRun>',
    '',
  ].join('\n')
}

function write(content) {
  const file = join(root, `run-${++counter}.trx`)
  writeFileSync(file, content)
  return file
}

function compare(deployResults, branchResults) {
  const a = typeof deployResults === 'string' ? deployResults : write(trx(deployResults))
  const b = typeof branchResults === 'string' ? branchResults : write(trx(branchResults))
  return spawnSync(process.execPath, [scriptPath, a, b], { encoding: 'utf8' })
}

const BASE = [
  ['SimpleFinance.UnitTests.Auth.LoginTests.RejectsBadPassword', 'Passed'],
  ['SimpleFinance.UnitTests.Auth.LoginTests.AcceptsGoodPassword', 'Passed'],
  ['SimpleFinance.UnitTests.Budget.BudgetTests.ComputesUsage(month: &quot;2026-01&quot;)', 'Passed'],
  ['SimpleFinance.UnitTests.Ai.SkippedTests.NeedsKey', 'NotExecuted'],
  [KNOWN, 'Failed'],
]

test('same pass set with only the known failure exits 0 and prints both counts', () => {
  const res = compare(BASE, [...BASE].reverse())
  assert.equal(res.status, 0, res.stdout + res.stderr)
  assert.match(res.stdout, /deploy: +3 passed, 1 failed, 1 skipped \(5 total\)/)
  assert.match(res.stdout, /branch: +3 passed, 1 failed, 1 skipped \(5 total\)/)
  assert.match(res.stdout, /ConcurrentSyncTests\.SerializedNonOverlappingSyncLogWindows/)
  assert.match(res.stdout, /^Result: SAME$/m)
})

test('decoded test names: entity-escaped parameters are compared as text', () => {
  const res = compare(BASE, BASE)
  assert.match(res.stdout, /0 differences/)
  assert.equal(res.status, 0)
})

test('a test passing on deploy but failing on the branch exits 1 and is named', () => {
  const branch = BASE.map(([n, o]) => (n.endsWith('AcceptsGoodPassword') ? [n, 'Failed'] : [n, o]))
  const res = compare(BASE, branch)
  assert.equal(res.status, 1)
  assert.match(res.stdout, /LoginTests\.AcceptsGoodPassword/)
  assert.match(res.stdout, /^Result: DIFFERENT$/m)
})

test('a test passing on deploy but missing on the branch exits 1 and is named', () => {
  const branch = BASE.filter(([n]) => !n.endsWith('RejectsBadPassword'))
  const res = compare(BASE, branch)
  assert.equal(res.status, 1)
  assert.match(res.stdout, /missing on branch.*\n.*LoginTests\.RejectsBadPassword/)
})

test('an extra passing test only on the branch also counts as a difference', () => {
  const res = compare(BASE, [...BASE, ['SimpleFinance.UnitTests.New.OnlyHere', 'Passed']])
  assert.equal(res.status, 1)
  assert.match(res.stdout, /New\.OnlyHere/)
})

test('an unknown failure present in both runs exits 1', () => {
  const runs = [...BASE, ['SimpleFinance.UnitTests.Reports.ExportTests.WritesCsv', 'Failed']]
  const res = compare(runs, runs)
  assert.equal(res.status, 1)
  assert.match(res.stdout, /not on the known list/)
  assert.match(res.stdout, /ExportTests\.WritesCsv/)
})

test('the known failure in only one run makes the failed sets differ (exit 1)', () => {
  const branch = BASE.map(([n, o]) => (n === KNOWN ? [n, 'Passed'] : [n, o]))
  const res = compare(BASE, branch)
  assert.equal(res.status, 1)
  assert.match(res.stdout, /SerializedNonOverlappingSyncLogWindows/)
})

test('an unreadable path exits 2', () => {
  const res = compare(join(root, 'does-not-exist.trx'), BASE)
  assert.equal(res.status, 2)
  assert.match(res.stderr, /cannot read/)
  assert.equal(res.stdout, '')
})

test('a file without UnitTestResult elements exits 2', () => {
  const empty = write('<?xml version="1.0"?><TestRun><Results></Results></TestRun>\n')
  const res = compare(BASE, empty)
  assert.equal(res.status, 2)
  assert.match(res.stderr, /no UnitTestResult/)
})

test('wrong argument count exits 2 with usage', () => {
  const res = spawnSync(process.execPath, [scriptPath, 'only-one.trx'], { encoding: 'utf8' })
  assert.equal(res.status, 2)
  assert.match(res.stderr, /usage/i)
})
