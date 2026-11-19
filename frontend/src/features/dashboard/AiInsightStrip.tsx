import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { Badge, Button, Col, Collapse, Row } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { useAiSettings, useDismissInsight, useInsights } from '@/features/ai/api'
import { useAuth } from '@/features/auth/useAuth'
import { AnomalyCard } from '@/features/dashboard/AnomalyCard'
import { InsightCard } from '@/features/dashboard/InsightCard'

/**
 * AI-INS-04: the collapsible proactive AI card strip at the top of the Dashboard. Reads the caller's
 * New insights (18-06 GET /api/ai/insights) and renders each by Kind — Anomaly→AnomalyCard, else
 * InsightCard — with Dispensar wired to useDismissInsight (invalidates ['ai','insights'] so a dismissed
 * card disappears and never returns).
 *
 * Gating (T-18-20): the ENTIRE strip is hidden when aiEnabled === false. Within an enabled account,
 * anomaly cards render only when aiAnomalyEnabled and insight cards only when aiInsightsEnabled — so a
 * user who turned a sub-feature off never sees its cards. When nothing qualifies, the strip renders
 * nothing (no empty shell).
 */
export function AiInsightStrip() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const [open, setOpen] = useState(true)

  const settingsQuery = useAiSettings(token)
  const insightsQuery = useInsights(token)
  const dismissInsight = useDismissInsight()

  const settings = settingsQuery.data

  // T-18-20: master switch off ⇒ the strip does not exist. Also stay hidden until settings load.
  if (!settings?.aiEnabled) {
    return null
  }

  const insights = insightsQuery.data ?? []
  const anomalies = settings.aiAnomalyEnabled ? insights.filter((i) => i.kind === 'Anomaly') : []
  const others = settings.aiInsightsEnabled ? insights.filter((i) => i.kind !== 'Anomaly') : []
  const visible = [...anomalies, ...others]

  if (visible.length === 0) {
    return null
  }

  const handleDismiss = (id: string) => {
    dismissInsight.mutate({ accessToken: token, id })
  }

  return (
    <div className="mb-3">
      <div className="d-flex align-items-center justify-content-between gap-2 mb-2">
        <div className="d-flex align-items-center gap-2">
          <IconifyIcon icon="lucide:lightbulb" className="text-warning" />
          <h6 className="mb-0">{t('insights.title')}</h6>
          <Badge bg="secondary" pill>
            {visible.length}
          </Badge>
        </div>
        <Button
          variant="link"
          size="sm"
          className="p-0 text-decoration-none d-flex align-items-center gap-1"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? t('insights.collapse') : t('insights.expand')}
          <IconifyIcon icon={open ? 'lucide:chevron-up' : 'lucide:chevron-down'} />
        </Button>
      </div>

      <Collapse in={open}>
        <div>
          <Row className="g-3 row-cols-1 row-cols-md-2 row-cols-xl-3">
            {anomalies.map((insight) => (
              <Col key={insight.id}>
                <AnomalyCard insight={insight} onDismiss={handleDismiss} isDismissing={dismissInsight.isPending} />
              </Col>
            ))}
            {others.map((insight) => (
              <Col key={insight.id}>
                <InsightCard insight={insight} onDismiss={handleDismiss} isDismissing={dismissInsight.isPending} />
              </Col>
            ))}
          </Row>
        </div>
      </Collapse>
    </div>
  )
}
