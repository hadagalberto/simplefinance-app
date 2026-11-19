import { Card, CardBody } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { MoneyText } from '@/components/MoneyText'
import { useAiSettings, useForecast, type AiForecastResponse } from '@/features/ai/api'
import { useAuth } from '@/features/auth/useAuth'

/** One parsed per-category driver row from the forecast's byCategoryJson blob. */
interface ForecastDriver {
  category: string
  projected: number
  shareOfExpense: number
}

/**
 * Reads the authoritative per-category drivers out of the deterministic byCategoryJson blob
 * (shape: { ..., drivers: [{ category, projected, shareOfExpense, ... }] }). NEVER parses the
 * untrusted narrative for numbers (T-19-15). Anything malformed yields an empty list — the card
 * still shows the projected balance + range from the response fields.
 */
function parseDrivers(byCategoryJson: string): ForecastDriver[] {
  try {
    const parsed: unknown = JSON.parse(byCategoryJson)
    if (parsed === null || typeof parsed !== 'object') {
      return []
    }
    const drivers = (parsed as { drivers?: unknown }).drivers
    if (!Array.isArray(drivers)) {
      return []
    }
    return drivers
      .filter(
        (d): d is { category: string; projected: number; shareOfExpense: number } =>
          d !== null &&
          typeof d === 'object' &&
          typeof (d as { category?: unknown }).category === 'string' &&
          typeof (d as { projected?: unknown }).projected === 'number' &&
          typeof (d as { shareOfExpense?: unknown }).shareOfExpense === 'number'
      )
      .map((d) => ({ category: d.category, projected: d.projected, shareOfExpense: d.shareOfExpense }))
  } catch {
    return []
  }
}

/** Current month as `YYYY-MM` (the forecast's default window). */
function currentMonthKey(): string {
  const now = new Date()
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
}

interface ForecastBodyProps {
  forecast: AiForecastResponse
}

function ForecastBody({ forecast }: ForecastBodyProps) {
  const { t } = useTranslation('ai')
  const drivers = parseDrivers(forecast.byCategoryJson)
  const percentFmt = new Intl.NumberFormat('pt-BR', { style: 'percent', maximumFractionDigits: 0 })

  return (
    <div className="d-flex flex-column gap-3">
      <div>
        <p className="text-muted small fw-semibold mb-1">{t('forecast.projectedBalance')}</p>
        <MoneyText
          amount={forecast.projectedBalance}
          variant={forecast.projectedBalance < 0 ? 'expense' : 'income'}
          showSign={false}
          className="fs-3 fw-semibold"
        />
        <p className="text-muted small mb-0">
          {t('forecast.range')}:{' '}
          <MoneyText amount={forecast.balanceLow} variant="neutral" className="fw-semibold" />
          {' – '}
          <MoneyText amount={forecast.balanceHigh} variant="neutral" className="fw-semibold" />
        </p>
      </div>

      <div className="d-flex gap-4 flex-wrap">
        <div>
          <p className="text-muted small mb-1">{t('forecast.projectedIncome')}</p>
          <MoneyText amount={forecast.projectedIncome} variant="income" showSign={false} />
        </div>
        <div>
          <p className="text-muted small mb-1">{t('forecast.projectedExpense')}</p>
          <MoneyText amount={forecast.projectedExpense} variant="expense" showSign={false} />
        </div>
      </div>

      {drivers.length > 0 && (
        <div>
          <p className="text-muted small fw-semibold mb-2">{t('forecast.drivers')}</p>
          <ul className="list-unstyled mb-0 d-flex flex-column gap-1">
            {drivers.map((driver) => (
              <li
                key={driver.category}
                className="d-flex align-items-center justify-content-between gap-2 small"
              >
                <span className="text-truncate">
                  {driver.category}
                  <span className="text-muted ms-1">{percentFmt.format(driver.shareOfExpense)}</span>
                </span>
                <MoneyText amount={driver.projected} variant="expense" showSign={false} />
              </li>
            ))}
          </ul>
        </div>
      )}

      {forecast.narrative && <p className="text-muted small mb-0 fst-italic">{forecast.narrative}</p>}
    </div>
  )
}

/**
 * AI-FCT-02: the Dashboard month-forecast card — projected end-of-month balance, its variance
 * range (balanceLow–balanceHigh), the top per-category drivers, and an optional model narrative.
 *
 * All numbers render from the authoritative response JSON (projected* fields + parsed
 * byCategoryJson drivers); the narrative is escaped React text and never supplies a figure
 * (T-19-15). Gating (T-19-16): the ENTIRE widget returns null unless aiEnabled && aiForecastEnabled.
 * A 404/no-forecast month renders a subtle empty state, never an error banner.
 */
export function ForecastWidget() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const settings = useAiSettings(token).data
  const forecastQuery = useForecast(token, currentMonthKey())

  // T-19-16: master switch or the forecast sub-flag off ⇒ the widget does not exist.
  if (!settings?.aiEnabled || !settings.aiForecastEnabled) {
    return null
  }

  const forecast = forecastQuery.data

  return (
    <Card className="mb-3">
      <CardBody>
        <h6 className="mb-3">{t('forecast.title')}</h6>
        {forecast ? (
          <ForecastBody forecast={forecast} />
        ) : (
          <p className="text-muted small mb-0">{t('forecast.empty')}</p>
        )}
      </CardBody>
    </Card>
  )
}
