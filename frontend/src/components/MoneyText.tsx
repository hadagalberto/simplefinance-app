/**
 * Central money-formatting primitive — every money figure in the app routes
 * through this component. Ported from v1 (frontend/src/components/MoneyText.tsx).
 * The color and number classes (`.text-income`/`.text-expense`/`.text-transfer`/
 * `.tabular-nums`) come from the authored stylesheet `src/styles/_tokens.scss`,
 * where they read the per-mode `--sf-*` tokens, and are combined via `clsx`.
 *
 * Formatting contract (matches v1 exactly):
 * - `amount={1234.56} variant="income"`  -> "+ R$ 1.234,56" colored `.text-income`
 * - `amount={1234.56} variant="expense"` -> "- R$ 1.234,56" colored `.text-expense`
 * - `amount={1234.56} variant="transfer"` -> "R$ 1.234,56" colored `.text-transfer`, no sign
 * - `amount={1234.56} variant="neutral"` (default) -> "R$ 1.234,56", no color override, no sign
 *
 * `showSign` is an explicit override: defaults to `true` for income/expense and
 * `false` for transfer/neutral, but a caller can force it off even on a signed
 * variant (e.g. an account-balance display shown as neutral/unsigned regardless
 * of the underlying number's sign).
 *
 * Formatting always goes through `Intl.NumberFormat('pt-BR', { style: 'currency',
 * currency })` and always applies the `.tabular-nums` utility class alongside the
 * variant's color class.
 */

import clsx from 'clsx'

export interface MoneyTextProps {
  amount: number
  currency?: string
  variant?: 'income' | 'expense' | 'transfer' | 'neutral'
  showSign?: boolean
  className?: string
}

const VARIANT_COLOR_CLASS: Record<NonNullable<MoneyTextProps['variant']>, string | undefined> = {
  income: 'text-income',
  expense: 'text-expense',
  transfer: 'text-transfer',
  neutral: undefined,
}

const DEFAULT_SHOW_SIGN: Record<NonNullable<MoneyTextProps['variant']>, boolean> = {
  income: true,
  expense: true,
  transfer: false,
  neutral: false,
}

export function MoneyText({
  amount,
  currency = 'BRL',
  variant = 'neutral',
  showSign,
  className,
}: MoneyTextProps) {
  const resolvedShowSign = showSign ?? DEFAULT_SHOW_SIGN[variant]

  const formatted = new Intl.NumberFormat('pt-BR', {
    style: 'currency',
    currency,
  }).format(Math.abs(amount))

  const sign = resolvedShowSign && (variant === 'income' || variant === 'expense')
    ? variant === 'income'
      ? '+ '
      : '- '
    : ''

  const colorClass = VARIANT_COLOR_CLASS[variant]

  return (
    <span className={clsx('tabular-nums', colorClass, className)}>
      {sign}
      {formatted}
    </span>
  )
}
