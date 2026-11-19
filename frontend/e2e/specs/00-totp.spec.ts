// Known answers for the test TOTP helper, from RFC 6238 appendix B (SHA1 rows),
// reduced to the 6 digits the API uses. No page is opened.
import { test, expect } from '@playwright/test'

import { base32Decode, msUntilNextStep, totp } from '../support/totp'

// ASCII "12345678901234567890" in base32.
const RFC_SECRET = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ'

test('RFC 6238 SHA1 known answers (6-digit forms)', () => {
  expect(base32Decode(RFC_SECRET).toString('ascii')).toBe('12345678901234567890')
  expect(base32Decode('gezd gnbv gy3t qojq gezd gnbv gy3t qojq====').toString('ascii')).toBe('12345678901234567890')

  // T = 59 s: 8-digit 94287082.
  expect(totp(RFC_SECRET, 59_000)).toBe('287082')
  // T = 1111111109 s: 8-digit 07081804, so the 6-digit form keeps its leading zero.
  expect(totp(RFC_SECRET, 1_111_111_109_000)).toBe('081804')

  expect(msUntilNextStep(59_000)).toBe(1_000)
  expect(msUntilNextStep(60_000)).toBe(30_000)
})
