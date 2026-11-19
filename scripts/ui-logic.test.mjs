// Tests for the import-free UI logic modules — run with: node --test scripts/ui-logic.test.mjs
//
// frontend/src/features/auth/authInputLogic.ts and
// frontend/src/features/transactions/pageWindow.ts have no imports, so Node
// loads them directly and strips their TypeScript types. frontend/package.json
// declares "type": "module", so Node treats them as ES modules without a
// MODULE_TYPELESS_PACKAGE_JSON warning.

import test from 'node:test'
import assert from 'node:assert/strict'

import { fillOtpDigits, scorePassword, strengthLevel } from '../frontend/src/features/auth/authInputLogic.ts'
import { pageWindow } from '../frontend/src/features/transactions/pageWindow.ts'

const EMPTY_CODE = ['', '', '', '', '', '']

test('scorePassword: empty and short passwords without other traits score 0', () => {
  assert.equal(scorePassword(''), 0)
  assert.equal(scorePassword('short'), 0)
})

test('scorePassword: a digit alone under 8 characters scores 1', () => {
  assert.equal(scorePassword('1234567'), 1)
})

test('scorePassword: each trait adds one point', () => {
  assert.equal(scorePassword('abcdefgh'), 1) // length 8
  assert.equal(scorePassword('Abcdefgh'), 2) // + mixed case
  assert.equal(scorePassword('Abcdefg1'), 3) // + digit
  assert.equal(scorePassword('Abcdefg1!'), 4) // + symbol
})

test('scorePassword: five points clamp to 4', () => {
  assert.equal(scorePassword('Abcdefghij1!'), 4)
})

test('strengthLevel: maps scores to the four level names', () => {
  assert.equal(strengthLevel(0), 'weak')
  assert.equal(strengthLevel(1), 'weak')
  assert.equal(strengthLevel(2), 'fair')
  assert.equal(strengthLevel(3), 'good')
  assert.equal(strengthLevel(4), 'strong')
})

test('fillOtpDigits: keeps only the digits of the pasted text', () => {
  assert.deepEqual(fillOtpDigits(EMPTY_CODE, 0, '12 34-56'), ['1', '2', '3', '4', '5', '6'])
})

test('fillOtpDigits: writes from the start index and ignores overflow', () => {
  assert.deepEqual(fillOtpDigits(['1', '2', '', '', '', ''], 4, '987'), ['1', '2', '', '', '9', '8'])
})

test('fillOtpDigits: text without digits returns an equal copy and never mutates the input', () => {
  const code = ['1', '', '3', '', '', '']
  const snapshot = [...code]
  const result = fillOtpDigits(code, 0, 'abc')
  assert.deepEqual(result, snapshot)
  assert.notEqual(result, code)
  assert.deepEqual(code, snapshot)

  fillOtpDigits(code, 0, '999999')
  assert.deepEqual(code, snapshot)
})

test('pageWindow: no pages and a single page', () => {
  assert.deepEqual(pageWindow(0, 0), [])
  assert.deepEqual(pageWindow(1, 0), [0])
})

test('pageWindow: 7 or fewer pages are all shown', () => {
  assert.deepEqual(pageWindow(5, 2), [0, 1, 2, 3, 4])
  assert.deepEqual(pageWindow(7, 0), [0, 1, 2, 3, 4, 5, 6])
})

test('pageWindow: first page of many', () => {
  assert.deepEqual(pageWindow(20, 0), [0, 1, 'gap', 19])
})

test('pageWindow: near the start', () => {
  assert.deepEqual(pageWindow(20, 2), [0, 1, 2, 3, 'gap', 19])
})

test('pageWindow: in the middle, gaps on both sides', () => {
  assert.deepEqual(pageWindow(20, 10), [0, 'gap', 9, 10, 11, 'gap', 19])
})

test('pageWindow: last page, and an out-of-range current page is clamped', () => {
  assert.deepEqual(pageWindow(20, 19), [0, 'gap', 18, 19])
  assert.deepEqual(pageWindow(20, 99), [0, 'gap', 18, 19])
})

test('pageWindow: a single hidden page is shown instead of a gap', () => {
  assert.deepEqual(pageWindow(20, 3), [0, 1, 2, 3, 4, 'gap', 19])
})

test('pageWindow: never more than 7 slots', () => {
  for (let current = 0; current < 50; current++) {
    assert.ok(pageWindow(50, current).length <= 7, `current ${current}`)
  }
})
