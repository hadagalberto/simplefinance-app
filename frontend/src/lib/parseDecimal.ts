// Locale-tolerant decimal parsing for typed amounts.
//
// Native <input type="number"> reads the text with the browser's UI language,
// not the app's: in an English browser "1234,56" silently became 123456. Amount
// fields are therefore plain text inputs, and this parser turns what the user
// typed into a number, whichever separator they used.
//
// Kept free of imports so node:test can load it directly (scripts/).

export type DecimalSeparator = ',' | '.'

/** Decimal separator of a BCP 47 locale ("pt-BR" -> ",", "en" -> "."). */
export function decimalSeparatorFor(locale: string | undefined): DecimalSeparator {
  try {
    const part = new Intl.NumberFormat(locale || undefined).formatToParts(1.5).find((p) => p.type === 'decimal')
    return part?.value === '.' ? '.' : ','
  } catch {
    return ','
  }
}

const GROUPED_INTEGER = /^\d{1,3}(\d{3})*$/

function isGrouped(parts: string[]): boolean {
  // "1.234.567" -> ["1", "234", "567"]: first group 1-3 digits, the rest exactly 3.
  return parts.length > 1 && /^\d{1,3}$/.test(parts[0]) && parts.slice(1).every((p) => /^\d{3}$/.test(p))
}

/**
 * Parses a typed decimal such as "1234,56", "1.234,56", "1234.56", "1,234.56"
 * or "R$ 1.234,56". Returns null when the text is empty or not a number.
 *
 * Rules:
 * - Both "," and "." present: the last one is the decimal separator, the other
 *   one groups thousands.
 * - Only one kind, appearing more than once: it groups thousands.
 * - Only one kind, appearing once: it is the decimal separator, except when it
 *   is followed by exactly 3 digits after a 1-3 digit integer part ("1.234",
 *   "1,234"). That case is ambiguous, so `localeDecimal` decides: the
 *   separator that is not the locale's decimal one is read as grouping.
 */
export function parseDecimal(raw: unknown, localeDecimal: DecimalSeparator = ','): number | null {
  if (typeof raw === 'number') return Number.isFinite(raw) ? raw : null
  if (typeof raw !== 'string') return null

  // \s also covers the no-break spaces (U+00A0, U+202F) that Intl inserts.
  let text = raw.replace(/\s/g, '').replace(/^R\$|^\$|^US\$/i, '')
  let sign = ''
  if (text.startsWith('-') || text.startsWith('+')) {
    sign = text[0] === '-' ? '-' : ''
    text = text.slice(1)
  }
  text = text.replace(/^R\$|^\$|^US\$/i, '')
  if (text === '' || !/^[\d.,]+$/.test(text) || !/\d/.test(text)) return null

  const lastComma = text.lastIndexOf(',')
  const lastDot = text.lastIndexOf('.')
  let integer: string
  let fraction = ''

  if (lastComma >= 0 && lastDot >= 0) {
    const decimal = lastComma > lastDot ? ',' : '.'
    const group = decimal === ',' ? '.' : ','
    const at = text.lastIndexOf(decimal)
    integer = text.slice(0, at)
    fraction = text.slice(at + 1)
    if (fraction.includes(group) || fraction.includes(decimal)) return null
    const groups = integer.split(group)
    if (groups.length > 1 && !isGrouped(groups)) return null
    integer = groups.join('')
  } else {
    const sep = lastComma >= 0 ? ',' : lastDot >= 0 ? '.' : ''
    if (sep === '') {
      integer = text
    } else {
      const parts = text.split(sep)
      if (parts.length > 2) {
        if (!isGrouped(parts)) return null
        integer = parts.join('')
      } else {
        const [left, right] = parts
        const ambiguous = right.length === 3 && GROUPED_INTEGER.test(left) && left.length <= 3 && !/^0/.test(left)
        if (ambiguous && sep !== localeDecimal) {
          integer = left + right
        } else {
          integer = left
          fraction = right
        }
      }
    }
  }

  if (integer === '' && fraction === '') return null
  const normalized = `${sign}${integer === '' ? '0' : integer}${fraction !== '' ? `.${fraction}` : ''}`
  if (!/^-?\d+(\.\d+)?$/.test(normalized)) return null
  const value = Number(normalized)
  return Number.isFinite(value) ? value : null
}
