import type { ReactNode } from 'react'
import { Alert, Button, Card, CardBody } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

/**
 * Shared Card wrapper every dashboard/budget/investments/net-worth chart
 * region uses. Enforces a consistent min-height content area so ApexChart's
 * percentage height always has a real pixel height to resolve against on
 * first paint, and the loading skeleton matches the loaded chart's footprint
 * (no layout shift). Ported from v1's frontend/src/components/charts/ChartCard.tsx
 * loading/error/empty/content contract.
 *
 * Never wrap `children` in `overflow-x-auto` here: v1's Recharts
 * ResponsiveContainer hit an infinite measure->render->remeasure resize loop
 * (visible flicker) when the measured SVG width toggled a horizontal
 * scrollbar on/off inside an overflow container. ApexCharts does not have
 * that specific failure mode, but a stable min-height container is still
 * required for percentage height to resolve on first paint.
 */
export interface ChartCardProps {
  title: string
  subheading?: string
  action?: ReactNode
  isLoading?: boolean
  isError?: boolean
  onRetry?: () => void
  isEmpty?: boolean
  emptyMessage?: ReactNode
  children?: ReactNode
}

export function ChartCard({ title, subheading, action, isLoading = false, isError = false, onRetry, isEmpty = false, emptyMessage, children }: ChartCardProps) {
  const { t } = useTranslation(['shell', 'common'])

  return (
    <Card className="h-100">
      <CardBody>
        <div className="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
          <div>
            <h5 className="mb-0">{title}</h5>
            {subheading ? <p className="text-muted small mb-0">{subheading}</p> : null}
          </div>
          {action ? <div>{action}</div> : null}
        </div>

        {isLoading ? (
          <div className="placeholder-glow" style={{ minHeight: 260 }}>
            <span className="placeholder col-12" style={{ height: 260, display: 'block', borderRadius: 'var(--bs-border-radius)' }} />
          </div>
        ) : isError ? (
          <Alert variant="danger" className="d-flex flex-column align-items-center justify-content-center text-center gap-3 mb-0" style={{ minHeight: 260 }}>
            <span>{t('shell:chartCard.loadError')}</span>
            {onRetry ? (
              <Button variant="outline-danger" size="sm" onClick={onRetry}>
                {t('common:actions.retry')}
              </Button>
            ) : null}
          </Alert>
        ) : isEmpty ? (
          <div className="d-flex align-items-center justify-content-center text-center text-muted small" style={{ minHeight: 260 }}>
            {emptyMessage}
          </div>
        ) : (
          <div style={{ minHeight: 260 }}>{children}</div>
        )}
      </CardBody>
    </Card>
  )
}
