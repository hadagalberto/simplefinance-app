import { useMemo, useState } from 'react'
import { Button, ButtonGroup, Card, CardBody } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import {
  useAiSettings,
  useMonthlySummary,
  useResendSummary,
  type AiSummaryResponse,
} from '@/features/ai/api'
import { useAuth } from '@/features/auth/useAuth'

/** Parsed figures pulled from the authoritative dataJson fact package (never the prose). */
interface SummaryFigures {
  income: number | null
  expense: number | null
  balance: number | null
  netWorthChange: number | null
  topCategories: Array<{ category: string; amount: number }>
}

function readNumber(source: Record<string, unknown>, key: string): number | null {
  const value = source[key]
  return typeof value === 'number' ? value : null
}

/**
 * Reads totals + net-worth change + top categories from the dataJson fact package
 * ({ totals: { income, expense, balance }, netWorth: { change }, byCategory: [...] }).
 * NEVER parses the untrusted title/body for numbers (T-19-15). Malformed JSON yields
 * all-null figures — the section still renders the title/body prose.
 */
function parseFigures(dataJson: string): SummaryFigures {
  const empty: SummaryFigures = {
    income: null,
    expense: null,
    balance: null,
    netWorthChange: null,
    topCategories: [],
  }
  try {
    const parsed: unknown = JSON.parse(dataJson)
    if (parsed === null || typeof parsed !== 'object') {
      return empty
    }
    const root = parsed as Record<string, unknown>
    const totals =
      root.totals !== null && typeof root.totals === 'object'
        ? (root.totals as Record<string, unknown>)
        : {}
    const netWorth =
      root.netWorth !== null && typeof root.netWorth === 'object'
        ? (root.netWorth as Record<string, unknown>)
        : {}
    const byCategory = Array.isArray(root.byCategory) ? root.byCategory : []
    const topCategories = byCategory
      .filter(
        (c): c is { category: string; amount: number } =>
          c !== null &&
          typeof c === 'object' &&
          typeof (c as { category?: unknown }).category === 'string' &&
          typeof (c as { amount?: unknown }).amount === 'number'
      )
      .slice(0, 5)
      .map((c) => ({ category: c.category, amount: c.amount }))

    return {
      income: readNumber(totals, 'income'),
      expense: readNumber(totals, 'expense'),
      balance: readNumber(totals, 'balance'),
      netWorthChange: readNumber(netWorth, 'change'),
      topCategories,
    }
  } catch {
    return empty
  }
}

/** `YYYY-MM` for `monthsBack` months before the current UTC month (0 = current, 1 = prior). */
function monthKey(monthsBack: number): string {
  const now = new Date()
  const date = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - monthsBack, 1))
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`
}

interface SummaryFiguresViewProps {
  dataJson: string
}

function SummaryFiguresView({ dataJson }: SummaryFiguresViewProps) {
  const { t } = useTranslation('ai')
  const figures = useMemo(() => parseFigures(dataJson), [dataJson])

  return (
    <div className="d-flex flex-column gap-3">
      <div className="d-flex gap-4 flex-wrap">
        {figures.income !== null && (
          <div>
            <p className="text-muted small mb-1">{t('summary.income')}</p>
            <MoneyText amount={figures.income} variant="income" showSign={false} />
          </div>
        )}
        {figures.expense !== null && (
          <div>
            <p className="text-muted small mb-1">{t('summary.expense')}</p>
            <MoneyText amount={figures.expense} variant="expense" showSign={false} />
          </div>
        )}
        {figures.balance !== null && (
          <div>
            <p className="text-muted small mb-1">{t('summary.balance')}</p>
            <MoneyText amount={figures.balance} variant={figures.balance < 0 ? 'expense' : 'income'} />
          </div>
        )}
        {figures.netWorthChange !== null && (
          <div>
            <p className="text-muted small mb-1">{t('summary.netWorthChange')}</p>
            <MoneyText amount={figures.netWorthChange} variant="neutral" />
          </div>
        )}
      </div>

      {figures.topCategories.length > 0 && (
        <div>
          <p className="text-muted small fw-semibold mb-2">{t('summary.topCategories')}</p>
          <ul className="list-unstyled mb-0 d-flex flex-column gap-1">
            {figures.topCategories.map((item) => (
              <li key={item.category} className="d-flex align-items-center justify-content-between gap-2 small">
                <span className="text-truncate">{item.category}</span>
                <MoneyText amount={item.amount} variant="expense" showSign={false} />
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

interface SummaryContentProps {
  summary: AiSummaryResponse
  token: string
}

function SummaryContent({ summary, token }: SummaryContentProps) {
  const { t } = useTranslation('ai')
  const { showNotification } = useNotificationContext()
  const resend = useResendSummary()

  const handleResend = () => {
    resend.mutate(
      { accessToken: token, id: summary.id },
      {
        onSuccess: () => showNotification({ message: t('summary.resendSuccess'), variant: 'success' }),
        onError: () => showNotification({ message: t('summary.resendError'), variant: 'danger' }),
      }
    )
  }

  return (
    <div className="d-flex flex-column gap-3">
      <div className="d-flex align-items-start justify-content-between gap-2">
        {/* title/body are untrusted model prose — rendered as React text children (escaped), never HTML (T-19-15). */}
        <h6 className="mb-0">{summary.title}</h6>
        <Button variant="outline-secondary" size="sm" disabled={resend.isPending} onClick={handleResend}>
          {resend.isPending ? t('summary.resending') : t('summary.resend')}
        </Button>
      </div>

      <p className="text-muted mb-0" style={{ whiteSpace: 'pre-line' }}>
        {summary.body}
      </p>

      <SummaryFiguresView dataJson={summary.dataJson} />
    </div>
  )
}

/**
 * AI-FCT-04: the Reports monthly-summary section — the persisted summary's title + body prose,
 * a figures view from the authoritative dataJson, and a "Reenviar" button that re-pushes the
 * STORED summary via Telegram/e-mail (no regeneration).
 *
 * Gating (T-19-16): the ENTIRE section returns null unless aiEnabled && aiMonthlySummaryEnabled.
 * A 404/no-summary month renders an "ainda não disponível" empty state, never an error banner.
 * A month selector switches between the prior month (default — the day-1 job's target) and the
 * current month.
 */
export function MonthlySummarySection() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  // 0 = current month, 1 = prior month (default — the summary is generated for the prior month).
  const [monthsBack, setMonthsBack] = useState(1)

  const settings = useAiSettings(token).data
  const month = monthKey(monthsBack)
  const summaryQuery = useMonthlySummary(token, month)

  // T-19-16: master switch or the monthly-summary sub-flag off ⇒ the section does not exist.
  if (!settings?.aiEnabled || !settings.aiMonthlySummaryEnabled) {
    return null
  }

  const summary = summaryQuery.data

  return (
    <Card className="mb-3">
      <CardBody>
        <div className="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
          <h5 className="mb-0">{t('summary.title')}</h5>
          <ButtonGroup size="sm">
            <Button
              variant={monthsBack === 1 ? 'primary' : 'outline-secondary'}
              onClick={() => setMonthsBack(1)}
            >
              {t('summary.priorMonth')}
            </Button>
            <Button
              variant={monthsBack === 0 ? 'primary' : 'outline-secondary'}
              onClick={() => setMonthsBack(0)}
            >
              {t('summary.currentMonth')}
            </Button>
          </ButtonGroup>
        </div>

        {summary ? (
          <SummaryContent summary={summary} token={token} />
        ) : (
          <p className="text-muted small mb-0">{t('summary.empty')}</p>
        )}
      </CardBody>
    </Card>
  )
}
