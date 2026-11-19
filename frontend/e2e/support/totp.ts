// RFC 6238 time-based one-time passwords for the account-security spec, on
// node:crypto only: HMAC-SHA1, 30 second steps, 6 digits. These are the
// parameters the API verifies with (Otp.NET defaults, see TotpService).
//
// The secret and the codes are sensitive even on the throwaway stack; callers
// must never log or annotate them.
import { createHmac } from 'node:crypto'

const BASE32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
const STEP_MS = 30_000
const DIGITS = 6

/** Decodes RFC 4648 base32. Case-insensitive; spaces and "=" padding are ignored. */
export function base32Decode(secret: string): Buffer {
  const clean = secret.toUpperCase().replace(/[\s=]/g, '')
  const bytes: number[] = []
  let buffer = 0
  let bits = 0
  for (const char of clean) {
    const value = BASE32_ALPHABET.indexOf(char)
    if (value === -1) throw new Error('base32Decode: the secret holds a character outside the base32 alphabet')
    buffer = (buffer << 5) | value
    bits += 5
    if (bits >= 8) {
      bits -= 8
      bytes.push((buffer >> bits) & 0xff)
    }
  }
  return Buffer.from(bytes)
}

/** The 6-digit code for the 30 s step that contains `atMs`. */
export function totp(secret: string, atMs: number = Date.now()): string {
  const counter = Math.floor(atMs / STEP_MS)
  const message = Buffer.alloc(8)
  message.writeBigUInt64BE(BigInt(counter))
  const digest = createHmac('sha1', base32Decode(secret)).update(message).digest()
  // Dynamic truncation (RFC 4226 section 5.3).
  const offset = digest[digest.length - 1] & 0x0f
  const binary = digest.readUInt32BE(offset) & 0x7fffffff
  return String(binary % 10 ** DIGITS).padStart(DIGITS, '0')
}

/** The 30 s step number that contains `atMs`. */
export function stepOf(atMs: number = Date.now()): number {
  return Math.floor(atMs / STEP_MS)
}

/** Milliseconds left in the current 30 s step. */
export function msUntilNextStep(atMs: number = Date.now()): number {
  return STEP_MS - (atMs % STEP_MS)
}
