// Runs once before any project. Creates the test identity for this run and
// checks that the throwaway stack is fresh (no user yet), because first-run
// setup only works while the Users table is empty.
//
// The identity lives only in process.env, which Playwright hands to the
// workers. It is never written to disk and never logged.
import { randomBytes } from 'node:crypto'

import { stackSql } from './support/db'

const API_HEALTH_URL = 'http://localhost:5080/health'
const NOT_FRESH = 'sfverify stack is not fresh - run npm --prefix frontend run e2e (it resets the throwaway stack)'

function generatePassword(): string {
  // 32 base64url chars plus one of each class, so any password policy finds what it wants.
  return `${randomBytes(24).toString('base64url')}Aa9!`
}

export default async function globalSetup() {
  process.env.E2E_NAME = 'E2E Tester'
  process.env.E2E_EMAIL = `e2e-${randomBytes(6).toString('hex')}@example.com`
  process.env.E2E_PASSWORD = generatePassword()

  let healthy: boolean
  try {
    healthy = (await fetch(API_HEALTH_URL, { signal: AbortSignal.timeout(5000) })).status === 200
  } catch {
    healthy = false
  }
  if (!healthy) throw new Error(NOT_FRESH)

  let users: string
  try {
    users = stackSql('select count(*) from "Users"')
  } catch {
    throw new Error(NOT_FRESH)
  }
  if (users !== '0') throw new Error(NOT_FRESH)
}
