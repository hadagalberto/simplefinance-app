#!/usr/bin/env node
/**
 * compare-test-runs.mjs — proves two .NET test runs pass exactly the same tests.
 *
 * Purpose
 *   VERIFY-04 needs the backend suite on this branch to pass the same tests as
 *   on the `deploy` branch. Both runs are written with
 *   `dotnet test --logger trx`; this script reads every UnitTestResult element
 *   (testName + outcome, in any attribute order) from the two TRX files and
 *   compares the passed and failed sets.
 *
 * Usage
 *   node scripts/compare-test-runs.mjs <deploy.trx> <branch.trx>
 *
 * Exit codes
 *   0  passed sets equal, failed sets equal, and every failure is on the known
 *      environment list below
 *   1  any difference between the runs, or a failure that is not on the list
 *   2  wrong arguments, an unreadable file, or a file with no UnitTestResult
 *
 * Known environment failures
 *   Matched as a suffix of the fully qualified test name. They depend on the
 *   local machine, not on the code; they are accepted only when they fail in
 *   both runs.
 */

import { readFileSync } from 'node:fs'
import process from 'node:process'

const KNOWN_ENVIRONMENT_FAILURES = ['ConcurrentSyncTests.SerializedNonOverlappingSyncLogWindows']

const FAILED_OUTCOMES = new Set(['Failed', 'Error', 'Timeout', 'Aborted'])

class InputError extends Error {}

function decodeXml(text) {
  return text
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, dec) => String.fromCodePoint(Number(dec)))
    .replace(/&amp;/g, '&')
}

function attribute(tag, name) {
  const m = tag.match(new RegExp(`\\s${name}="([^"]*)"`))
  return m ? decodeXml(m[1]) : undefined
}

/** Returns { passed, failed, skipped } as sorted name arrays. */
function readRun(path) {
  let xml
  try {
    xml = readFileSync(path, 'utf8')
  } catch (err) {
    throw new InputError(`cannot read ${path}: ${err.code ?? err.message}`)
  }
  const passed = new Set()
  const failed = new Set()
  const skipped = new Set()
  const seen = new Map()
  let rows = 0
  for (const m of xml.matchAll(/<UnitTestResult\b[^>]*>/g)) {
    const name = attribute(m[0], 'testName')
    const outcome = attribute(m[0], 'outcome')
    if (name === undefined || outcome === undefined) continue
    rows++
    // Two rows with one display name are kept apart instead of collapsing.
    const n = (seen.get(name) ?? 0) + 1
    seen.set(name, n)
    const key = n === 1 ? name : `${name} #${n}`
    if (outcome === 'Passed') passed.add(key)
    else if (FAILED_OUTCOMES.has(outcome)) failed.add(key)
    else skipped.add(key)
  }
  if (rows === 0) throw new InputError(`no UnitTestResult with testName and outcome in ${path}`)
  return { passed, failed, skipped, total: rows }
}

const sorted = (iterable) => [...iterable].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
const minus = (a, b) => sorted([...a].filter((x) => !b.has(x)))
const isKnown = (name) => KNOWN_ENVIRONMENT_FAILURES.some((suffix) => name.endsWith(suffix))

function counts(label, run) {
  return `${label}: ${run.passed.size} passed, ${run.failed.size} failed, ${run.skipped.size} skipped (${run.total} total)`
}

function main(argv) {
  if (argv.length !== 2) {
    throw new InputError('usage: node scripts/compare-test-runs.mjs <deploy.trx> <branch.trx>')
  }
  const deploy = readRun(argv[0])
  const branch = readRun(argv[1])

  const sections = [
    ['passed on deploy, missing on branch', minus(deploy.passed, new Set([...branch.passed, ...branch.failed, ...branch.skipped]))],
    ['passed on deploy, not passed on branch', minus(deploy.passed, branch.passed).filter((n) => branch.failed.has(n) || branch.skipped.has(n))],
    ['passed on branch, not passed on deploy', minus(branch.passed, deploy.passed)],
    ['failed on deploy only', minus(deploy.failed, branch.failed)],
    ['failed on branch only', minus(branch.failed, deploy.failed)],
  ]
  const unknownFailures = sorted(new Set([...deploy.failed, ...branch.failed].filter((n) => !isKnown(n))))

  const lines = [counts('deploy', deploy), counts('branch', branch)]
  const sharedFailures = sorted([...deploy.failed].filter((n) => branch.failed.has(n)))
  lines.push(`Failed in both runs: ${sharedFailures.length}`)
  for (const name of sharedFailures) lines.push(`  ${name}${isKnown(name) ? '  (known environment failure)' : ''}`)

  let differences = 0
  for (const [title, names] of sections) {
    if (names.length === 0) continue
    differences += names.length
    lines.push(`${title}: ${names.length}`)
    for (const name of names) lines.push(`  ${name}`)
  }
  lines.push(`${differences} differences`)
  if (unknownFailures.length > 0) {
    lines.push(`Failures not on the known list: ${unknownFailures.length}`)
    for (const name of unknownFailures) lines.push(`  ${name}`)
  }
  const same = differences === 0 && unknownFailures.length === 0
  lines.push(`Result: ${same ? 'SAME' : 'DIFFERENT'}`)
  process.stdout.write(lines.join('\n') + '\n')
  process.exitCode = same ? 0 : 1
}

try {
  main(process.argv.slice(2))
} catch (err) {
  process.stderr.write(`compare-test-runs: ${err && err.message ? err.message : String(err)}\n`)
  process.exitCode = 2
}
