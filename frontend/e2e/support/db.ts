// Read-only helpers for specs and setup code that need to look at the
// throwaway database. Every call goes through e2e/stack.mjs, the suite's only
// Docker entry point.
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
export const stackScript = path.resolve(here, '..', 'stack.mjs')
export const frontendDir = path.resolve(here, '..', '..')

/** Runs one query against the throwaway database and returns psql's raw `-At` output, trimmed. */
export function stackSql(query: string): string {
  return execFileSync(process.execPath, [stackScript, 'sql', query], { encoding: 'utf8', cwd: frontendDir }).trim()
}

/** Runs one stack.mjs command with its output shown in the test log. */
export function stackRun(...args: string[]): void {
  execFileSync(process.execPath, [stackScript, ...args], { stdio: 'inherit', cwd: frontendDir })
}
