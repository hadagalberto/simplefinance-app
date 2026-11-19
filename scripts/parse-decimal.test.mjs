// Tests for frontend/src/lib/parseDecimal.ts — run with: node --test scripts/parse-decimal.test.mjs
//
// Node strips the TypeScript types on import, so the parser is tested as-is.
// The regression it guards: in a non-Portuguese browser, "1234,56" typed into
// an amount field was saved as 123456 (100x the value).

import test from 'node:test'
import assert from 'node:assert/strict'
import { decimalSeparatorFor, parseDecimal } from '../frontend/src/lib/parseDecimal.ts'

test('decimal comma and decimal point both parse, in either locale', () => {
  for (const sep of [',', '.']) {
    assert.equal(parseDecimal('1234,56', sep), 1234.56)
    assert.equal(parseDecimal('1234.56', sep), 1234.56)
    assert.equal(parseDecimal('0,5', sep), 0.5)
    assert.equal(parseDecimal('12.5', sep), 12.5)
    assert.equal(parseDecimal('80', sep), 80)
  }
})

test('thousands grouping with both separators', () => {
  assert.equal(parseDecimal('1.234,56'), 1234.56)
  assert.equal(parseDecimal('1,234.56', '.'), 1234.56)
  assert.equal(parseDecimal('1,234.56', ','), 1234.56)
  assert.equal(parseDecimal('1.234.567,89'), 1234567.89)
  assert.equal(parseDecimal('1.234.567'), 1234567)
  assert.equal(parseDecimal('1,234,567', '.'), 1234567)
})

test('ambiguous "1.234" / "1,234" follows the locale', () => {
  assert.equal(parseDecimal('1.234', ','), 1234)
  assert.equal(parseDecimal('1,234', ','), 1.234)
  assert.equal(parseDecimal('1,234', '.'), 1234)
  assert.equal(parseDecimal('1.234', '.'), 1.234)
  // A leading zero or 4+ integer digits cannot be grouping.
  assert.equal(parseDecimal('0.125', ','), 0.125)
  assert.equal(parseDecimal('1234.567', ','), 1234.567)
})

test('currency prefix, spaces and sign', () => {
  assert.equal(parseDecimal('R$ 1.234,56'), 1234.56)
  assert.equal(parseDecimal(' 1 234,56 '), 1234.56)
  assert.equal(parseDecimal('-50,25'), -50.25)
  assert.equal(parseDecimal('-R$ 10'), -10)
})

test('numbers pass through; junk and empty are null', () => {
  assert.equal(parseDecimal(42.5), 42.5)
  assert.equal(parseDecimal(Number.NaN), null)
  for (const bad of ['', '   ', 'abc', '1,2,3', '1.23.4', '12a', '1,234.5,6', '-', ',', undefined, null]) {
    assert.equal(parseDecimal(bad), null, `expected null for ${JSON.stringify(bad)}`)
  }
})

test('decimalSeparatorFor maps app languages', () => {
  assert.equal(decimalSeparatorFor('pt-BR'), ',')
  assert.equal(decimalSeparatorFor('en'), '.')
})
