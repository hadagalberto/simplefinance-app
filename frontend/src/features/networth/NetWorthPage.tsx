import { useState } from 'react'
import { Alert, Button, ButtonGroup, Card, CardBody, Col, Placeholder, Row } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { ChartCard } from '@/components/charts/ChartCard'
import { EvolutionLineChart } from '@/components/charts/EvolutionLineChart'
import { MoneyText } from '@/components/MoneyText'
import { StatTile } from '@/components/StatTile'
import { useAuth } from '@/features/auth/useAuth'
import { useNetWorthEvolutionQuery, useNetWorthSummaryQuery } from '@/features/networth/api'

type NetWorthRange = 30 | 90 | 365

const RANGE_OPTION_VALUES: { value: NetWorthRange; key: '30d' | '90d' | '365d' }[] = [
  { value: 30, key: '30d' },
  { value: 90, key: '90d' },
  { value: 365, key: '365d' },
]

function StatTileSkeleton() {
  return (
    <Col>
      <Card>
        <CardBody>
          <Placeholder as="p" animation="glow" className="mb-1">
            <Placeholder xs={7} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={4} style={{ height: 28 }} />
          </Placeholder>
        </CardBody>
      </Card>
    </Col>
  )
}

function CompositionSkeleton() {
  return (
    <Col>
      <Card>
        <CardBody className="d-flex flex-column gap-3">
          <div className="d-flex align-items-center justify-content-between">
            <Placeholder as="span" animation="glow">
              <Placeholder xs={4} />
            </Placeholder>
            <Placeholder as="span" animation="glow">
              <Placeholder xs={3} />
            </Placeholder>
          </div>
          <div className="d-flex align-items-center justify-content-between">
            <Placeholder as="span" animation="glow">
              <Placeholder xs={4} />
            </Placeholder>
            <Placeholder as="span" animation="glow">
              <Placeholder xs={3} />
            </Placeholder>
          </div>
          <div className="d-flex align-items-center justify-content-between">
            <Placeholder as="span" animation="glow">
              <Placeholder xs={4} />
            </Placeholder>
            <Placeholder as="span" animation="glow">
              <Placeholder xs={3} />
            </Placeholder>
          </div>
        </CardBody>
      </Card>
    </Col>
  )
}

/**
 * FE-NW-01: the full Net Worth screen -- an accented net worth stat tile, an
 * accounts/investments/card-debt composition breakdown, and an ApexCharts
 * evolution line (EvolutionLineChart, 1-point-safe from Plan 12-01) with a
 * 30/90/365-day range selector. Wired to v1.0 Phase 6's unchanged
 * GET /api/networth/summary and GET /api/networth/evolution endpoints.
 * Mirrors DashboardPage.tsx's summary-tile loading/error pattern and its
 * 6M/12M range-toggle ButtonGroup pattern exactly.
 */
export function NetWorthPage() {
  const { t } = useTranslation(['networth', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const [range, setRange] = useState<NetWorthRange>(30)

  const summaryQuery = useNetWorthSummaryQuery(token)
  const evolutionQuery = useNetWorthEvolutionQuery(token, range)

  const summary = summaryQuery.data
  const points = evolutionQuery.data?.points ?? []

  return (
    <>
      <h4 className="mb-3">{t('networth:pageTitle')}</h4>

      {summaryQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('networth:loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void summaryQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {summaryQuery.isPending && (
        <Row className="g-3 row-cols-1 row-cols-sm-2 mb-3">
          <StatTileSkeleton />
          <CompositionSkeleton />
        </Row>
      )}

      {!summaryQuery.isPending && !summaryQuery.isError && summary && (
        <Row className="g-3 row-cols-1 row-cols-sm-2 mb-3">
          <Col>
            <StatTile label={t('networth:stat.netWorth')} amount={summary.netWorth} accent />
          </Col>
          <Col>
            <Card>
              <CardBody className="d-flex flex-column gap-3">
                <div className="d-flex align-items-center justify-content-between">
                  <span className="text-muted small fw-semibold">{t('networth:composition.accounts')}</span>
                  <MoneyText amount={summary.accountsBalance} variant="neutral" />
                </div>
                <div className="d-flex align-items-center justify-content-between">
                  <span className="text-muted small fw-semibold">{t('networth:composition.investments')}</span>
                  <MoneyText amount={summary.investmentsBalance} variant="neutral" />
                </div>
                <div className="d-flex align-items-center justify-content-between">
                  <span className="text-muted small fw-semibold">{t('networth:composition.cardDebt')}</span>
                  <MoneyText amount={summary.creditCardDebt} variant="expense" />
                </div>
              </CardBody>
            </Card>
          </Col>
        </Row>
      )}

      <Row className="g-3 row-cols-1">
        <Col>
          <ChartCard
            title={t('networth:chart.title')}
            action={
              <ButtonGroup size="sm">
                {RANGE_OPTION_VALUES.map((option) => (
                  <Button
                    key={option.value}
                    variant={range === option.value ? 'primary' : 'outline-secondary'}
                    onClick={() => setRange(option.value)}
                  >
                    {t(`networth:chart.ranges.${option.key}`)}
                  </Button>
                ))}
              </ButtonGroup>
            }
            isLoading={evolutionQuery.isPending}
            isError={evolutionQuery.isError}
            onRetry={() => void evolutionQuery.refetch()}
            isEmpty={!evolutionQuery.isPending && points.length === 0}
            emptyMessage={t('networth:chart.emptyMessage')}
          >
            <EvolutionLineChart
              points={points.map((point) => ({ date: point.date, value: point.netWorth }))}
              valueLabel={t('networth:chart.valueLabel')}
            />
          </ChartCard>
        </Col>
      </Row>
    </>
  )
}
