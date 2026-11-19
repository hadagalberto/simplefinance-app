import i18n from '@/i18n'
import { decimalSeparatorFor, parseDecimal } from './parseDecimal'

/**
 * Parses a typed amount using the app's active language to settle the one
 * ambiguous case ("1.234"). Returns null for empty or invalid text.
 */
export function parseAmount(raw: unknown): number | null {
  return parseDecimal(raw, decimalSeparatorFor(i18n.language))
}

/**
 * react-hook-form `setValueAs` for amount text inputs: a number, or NaN when
 * the text is empty or invalid so the zod `number()` rule reports it.
 */
export function amountValueAs(raw: unknown): number {
  return parseAmount(raw) ?? Number.NaN
}
