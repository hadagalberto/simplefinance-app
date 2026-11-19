import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { Badge, Button, Card, CardBody } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import type { AiInsightResponse } from '@/features/ai/api'

export interface AnomalyCardProps {
  insight: AiInsightResponse
  onDismiss: (id: string) => void
  isDismissing?: boolean
}

/** Info|Warning|Critical → react-bootstrap Badge bg variant (drives the severity color). */
const SEVERITY_VARIANT: Record<string, string> = {
  Info: 'info',
  Warning: 'warning',
  Critical: 'danger',
}

/**
 * Renders the DataJson figures blob as compact key: value rows. Numbers get pt-BR
 * grouping; anything unparseable renders nothing. NEVER parses the untrusted Body —
 * figures come only from the deterministic DataJson (T-18-19).
 */
function parseFigures(dataJson: string): Array<[string, string]> {
  try {
    const parsed: unknown = JSON.parse(dataJson)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return []
    }
    return Object.entries(parsed as Record<string, unknown>)
      .filter(([, value]) => typeof value === 'number' || typeof value === 'string')
      .map(([key, value]) => [
        key,
        typeof value === 'number' ? value.toLocaleString('pt-BR') : String(value),
      ])
  } catch {
    return []
  }
}

/**
 * AI-INS-04: one detected-anomaly card (Kind=Anomaly). Same shape as InsightCard but with an
 * alert icon accent to distinguish a statistical anomaly (z-score/duplicate/subscription jump/
 * atypical category) from a routine insight. Body is rendered as text — React escapes it, so
 * merchant-derived prose can never inject markup (T-18-19). Figures come only from DataJson.
 */
export function AnomalyCard({ insight, onDismiss, isDismissing = false }: AnomalyCardProps) {
  const { t } = useTranslation('ai')
  const [showFigures, setShowFigures] = useState(false)
  const figures = parseFigures(insight.dataJson)
  const variant = SEVERITY_VARIANT[insight.severity] ?? 'secondary'

  return (
    <Card border={variant === 'danger' ? 'danger' : undefined}>
      <CardBody className="d-flex flex-column gap-2">
        <div className="d-flex align-items-start justify-content-between gap-2">
          <div className="d-flex align-items-center gap-2">
            <IconifyIcon icon="lucide:badge-alert" className={`text-${variant}`} />
            <span className="fw-semibold">{insight.title}</span>
          </div>
          <Badge bg={variant} className="text-uppercase">
            {t(`insights.severity.${insight.severity}`, insight.severity)}
          </Badge>
        </div>

        <p className="text-muted small mb-0">{insight.body}</p>

        {showFigures && figures.length > 0 && (
          <dl className="row mb-0 small">
            {figures.map(([key, value]) => (
              <div key={key} className="col-6 d-flex justify-content-between gap-2">
                <dt className="text-muted fw-normal text-truncate">{key}</dt>
                <dd className="mb-0 tabular-nums">{value}</dd>
              </div>
            ))}
          </dl>
        )}

        <div className="d-flex align-items-center justify-content-end gap-2">
          {figures.length > 0 && (
            <Button variant="link" size="sm" className="p-0 text-decoration-none" onClick={() => setShowFigures((v) => !v)}>
              {showFigures ? t('insights.hide') : t('insights.view')}
            </Button>
          )}
          <Button variant="outline-secondary" size="sm" disabled={isDismissing} onClick={() => onDismiss(insight.id)}>
            {t('insights.dismiss')}
          </Button>
        </div>
      </CardBody>
    </Card>
  )
}
