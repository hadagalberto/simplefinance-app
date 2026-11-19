// Pure helpers behind the auth form inputs. This module has no imports, so the
// node tests in scripts/ui-logic.test.mjs can load it directly.

export type PasswordScore = 0 | 1 | 2 | 3 | 4

export type StrengthLevel = 'weak' | 'fair' | 'good' | 'strong'

/**
 * Scores a password from 0 to 4. One point each for: at least 8 characters,
 * at least 12 characters, both lower- and upper-case letters, a digit, and a
 * character that is neither a letter nor a digit. The total is clamped to 4.
 */
export function scorePassword(password: string): PasswordScore {
  let points = 0
  if (password.length >= 8) points += 1
  if (password.length >= 12) points += 1
  if (/[a-z]/.test(password) && /[A-Z]/.test(password)) points += 1
  if (/[0-9]/.test(password)) points += 1
  if (/[^A-Za-z0-9]/.test(password)) points += 1
  return Math.min(points, 4) as PasswordScore
}

/** Names the strength band of a score: 0 and 1 are weak, then fair, good, strong. */
export function strengthLevel(score: number): StrengthLevel {
  if (score >= 4) return 'strong'
  if (score === 3) return 'good'
  if (score === 2) return 'fair'
  return 'weak'
}

/**
 * Returns a new code array with the digits of `text` written from `start`
 * onward. Characters that are not digits are dropped, and digits that do not
 * fit before the end of the array are ignored. The input array is not changed.
 */
export function fillOtpDigits(code: string[], start: number, text: string): string[] {
  const next = [...code]
  const digits = text.replace(/[^0-9]/g, '')
  let slot = Math.max(0, start)
  for (const digit of digits) {
    if (slot >= next.length) break
    next[slot] = digit
    slot += 1
  }
  return next
}
