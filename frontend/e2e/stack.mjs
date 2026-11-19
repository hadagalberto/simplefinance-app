#!/usr/bin/env node
// The only entry point the e2e suite uses to talk to Docker.
//
// Every command addresses the throwaway compose project `sfverify`, built from
// this working tree with the repo-root docker-compose.yml. The project name is
// a constant: it is never read from argv or the environment, so a typo or a
// stray variable can never point `down -v` at the real local stack.
//
// The throwaway API never receives Pluggy credentials: the compose child gets
// empty strings for the client id/secret and the webhook URL/secret, and
// `reset` checks the resolved config before it starts anything.
//
// Usage (from frontend/):
//   node e2e/stack.mjs reset     wipe the throwaway stack (volumes too), rebuild, wait for health
//   node e2e/stack.mjs seed      load e2e/seed.sql into the throwaway database
//   node e2e/stack.mjs sql "<q>" run one query, print the raw result (psql -At)
//   node e2e/stack.mjs status    list the throwaway containers
//   node e2e/stack.mjs down      stop the throwaway stack (keeps its volume)
//
// Nothing here prints environment values.

import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const PROJECT = 'sfverify'
// The real local stack. Only ever looked up (to refuse running), never addressed.
const REAL_PROJECT = 'simplefinance'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')
const composeFile = path.join(repoRoot, 'docker-compose.yml')
const seedFile = path.join(here, 'seed.sql')

const API_HEALTH_URL = 'http://localhost:5080/health'
const FRONT_URL = 'http://localhost:5173/'
const HEALTH_LIMIT_MS = 5 * 60 * 1000

// Credential and webhook variables blanked for the throwaway API. Pluggy__Environment
// is not a secret and stays as configured.
const BLANKED = ['Pluggy__ClientId', 'Pluggy__ClientSecret', 'Pluggy__WebhookPublicUrl', 'Pluggy__WebhookSecretValue']

// psql runs inside the postgres container; the single-quoted script lets the
// container's shell expand POSTGRES_USER/POSTGRES_DB, so Node never sees them.
const PSQL = 'psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'

function fail(message) {
  console.error(`[stack] ${message}`)
  process.exit(1)
}

function childEnv() {
  const env = { ...process.env }
  for (const name of BLANKED) env[name] = ''
  return env
}

function composeArgs(...rest) {
  return ['compose', '--project-directory', repoRoot, '-f', composeFile, '-p', PROJECT, ...rest]
}

/** Runs one compose command for the throwaway project. Output streams to the terminal unless captured. */
function compose(rest, { capture = false, input } = {}) {
  const result = spawnSync('docker', composeArgs(...rest), {
    env: childEnv(),
    input,
    encoding: 'utf8',
    stdio: [input === undefined ? 'inherit' : 'pipe', capture ? 'pipe' : 'inherit', capture ? 'pipe' : 'inherit'],
    maxBuffer: 64 * 1024 * 1024,
  })
  if (result.error) fail(`could not run docker: ${result.error.message}`)
  return result
}

function realStackRunning() {
  const result = spawnSync(
    'docker',
    ['ps', '-q', '--filter', `label=com.docker.compose.project=${REAL_PROJECT}`, '--filter', 'status=running'],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
  )
  if (result.error || result.status !== 0) fail('could not list running containers (is Docker running?)')
  return result.stdout.trim().length > 0
}

function assertPluggyBlanked() {
  const result = compose(['config', '--format', 'json'], { capture: true })
  if (result.status !== 0) fail('resolving the throwaway stack config failed')
  let config
  try {
    config = JSON.parse(result.stdout)
  } catch {
    fail('the resolved stack config is not valid JSON')
  }
  const environment = config?.services?.api?.environment ?? {}
  const leaked = BLANKED.filter((name) => (environment[name] ?? '') !== '')
  // Only variable names are reported, never their values.
  if (leaked.length) fail(`refusing to start: the throwaway API would receive ${leaked.join(', ')}`)
}

async function waitFor200(url, deadline) {
  for (;;) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(5000) })
      if (response.status === 200) return
    } catch {
      // Not up yet.
    }
    if (Date.now() > deadline) fail(`timed out waiting for ${url}`)
    await new Promise((resolve) => setTimeout(resolve, 2000))
  }
}

async function reset() {
  if (realStackRunning()) {
    fail(`containers of the "${REAL_PROJECT}" project are running. Stop that stack yourself, then retry; this script never touches it.`)
  }
  console.log(`[stack] resetting throwaway project "${PROJECT}"`)
  if (compose(['down', '-v', '--remove-orphans']).status !== 0) fail('down failed')
  assertPluggyBlanked()
  if (compose(['up', '-d', '--build']).status !== 0) fail('up --build failed')
  const deadline = Date.now() + HEALTH_LIMIT_MS
  await waitFor200(API_HEALTH_URL, deadline)
  await waitFor200(FRONT_URL, deadline)
  console.log('[stack] api and front answer 200')
}

function psql(sqlText, extraFlags = '') {
  return compose(['exec', '-T', 'postgres', 'sh', '-c', `${PSQL} ${extraFlags}`.trim()], { capture: true, input: sqlText })
}

function sql(query) {
  if (!query) fail('usage: node e2e/stack.mjs sql "<query>"')
  const result = psql(query, '-At')
  if (result.status !== 0) fail(`query failed: ${result.stderr.trim()}`)
  process.stdout.write(result.stdout)
}

const SEEDED_TABLES = [
  'Connections',
  'Accounts',
  'CreditCardDetails',
  'Transactions',
  'Budgets',
  'Investments',
  'NetWorthSnapshots',
]

function seed() {
  const text = readFileSync(seedFile, 'utf8')
  const result = psql(text)
  if (result.status !== 0) fail(`seed failed: ${result.stderr.trim()}`)
  const counts = psql(SEEDED_TABLES.map((t) => `select '${t}', count(*) from "${t}";`).join('\n'), '-At -F " "')
  if (counts.status !== 0) fail(`count query failed: ${counts.stderr.trim()}`)
  console.log(`[stack] seeded rows:\n${counts.stdout.trim()}`)
}

const [command, ...args] = process.argv.slice(2)
switch (command) {
  case 'reset':
    await reset()
    break
  case 'seed':
    seed()
    break
  case 'sql':
    sql(args.join(' '))
    break
  case 'status':
    process.exit(compose(['ps']).status ?? 1)
    break
  case 'down':
    process.exit(compose(['down']).status ?? 1)
    break
  default:
    fail('usage: node e2e/stack.mjs <reset|seed|sql "<query>"|status|down>')
}
