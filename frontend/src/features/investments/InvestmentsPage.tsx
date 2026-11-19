import { Alert, Button, Card, CardBody, Col, Placeholder, Row, Table } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { CategoryBarChart, type CategoryBarChartItem } from '@/components/charts/CategoryBarChart'
import { ChartCard } from '@/components/charts/ChartCard'
import { MoneyText } from '@/components/MoneyText'
import { StatTile } from '@/components/StatTile'
import { useAuth } from '@/features/auth/useAuth'

import {
  useInvestmentPositionsQuery,
  useInvestmentSummaryQuery,
  type InvestmentDistributionItem,
  type InvestmentPositionItem,
} from './api'

const EM_DASH = '—'

const dueDateFormatter = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' })

function formatDueDate(dueDate: string | null): string {
  if (!dueDate) return EM_DASH
  return dueDateFormatter.format(new Date(dueDate))
}

function toChartItems(items: InvestmentDistributionItem[]): CategoryBarChartItem[] {
  return items.map((item) => ({
    id: item.label,
    label: item.label,
    color: null,
    amount: item.value,
    percent: item.percent,
  }))
}

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

function RowSkeleton() {
  return (
    <tr>
      {Array.from({ length: 7 }).map((_, index) => (
        <td key={index}>
          <Placeholder as="span" animation="glow">
            <Placeholder xs={8} />
          </Placeholder>
        </td>
      ))}
    </tr>
  )
}

function PositionRow({ item }: { item: InvestmentPositionItem }) {
  const institutionOrIssuer = item.issuerName ?? item.institution ?? EM_DASH

  return (
    <tr>
      <td className="fw-semibold">{item.name}</td>
      <td>{institutionOrIssuer}</td>
      <td>{item.type}</td>
      <td>{item.value !== null ? <MoneyText amount={item.value} variant="neutral" /> : EM_DASH}</td>
      <td>{item.amountInvested !== null ? <MoneyText amount={item.amountInvested} variant="neutral" /> : EM_DASH}</td>
      <td>
        {item.gain !== null ? (
          <MoneyText amount={item.gain} variant={item.gain >= 0 ? 'income' : 'expense'} />
        ) : (
          EM_DASH
        )}
      </td>
      <td>{formatDueDate(item.dueDate)}</td>
    </tr>
  )
}

/**
 * FE-INV-01: the full Investments screen -- total invested/current
 * value/gain stat tiles, distribution-by-type and distribution-by-issuer
 * ApexCharts (CategoryBarChart), a detailed positions table, and a
 * full-page empty state when no investments have synced. Wired to
 * v1.0 Phase 6's unchanged GET /api/investments and
 * GET /api/investments/summary endpoints.
 */
export function InvestmentsPage() {
  const { t } = useTranslation(['investments', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const summaryQuery = useInvestmentSummaryQuery(token)
  const positionsQuery = useInvestmentPositionsQuery(token)

  const summary = summaryQuery.data
  const byTypeItems = toChartItems(summary?.byType ?? [])
  const byIssuerItems = toChartItems(summary?.byIssuer ?? [])
  const positionItems = positionsQuery.data?.items ?? []

  const isEmpty = !positionsQuery.isPending && !positionsQuery.isError && positionItems.length === 0

  if (isEmpty) {
    return (
      <div>
        <h4 className="mb-3">{t('investments:pageTitle')}</h4>
        <Card>
          <CardBody className="text-center py-5">
            <h5>{t('investments:empty.title')}</h5>
            <p className="text-muted mb-0">{t('investments:empty.description')}</p>
          </CardBody>
        </Card>
      </div>
    )
  }

  return (
    <div>
      <h4 className="mb-3">{t('investments:pageTitle')}</h4>

      {summaryQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('investments:loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void summaryQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      <Row className="row-cols-1 row-cols-md-3 g-3 mb-3">
        {summaryQuery.isPending ? (
          <>
            <StatTileSkeleton />
            <StatTileSkeleton />
            <StatTileSkeleton />
          </>
        ) : !summaryQuery.isError && summary ? (
          <>
            <Col>
              <StatTile label={t('investments:stats.totalInvested')} amount={summary.totalInvested} />
            </Col>
            <Col>
              <StatTile label={t('investments:stats.totalCurrentValue')} amount={summary.totalCurrentValue} />
            </Col>
            <Col>
              <StatTile
                label={t('investments:stats.totalGain')}
                amount={summary.totalGain}
                variant={summary.totalGain >= 0 ? 'income' : 'expense'}
              />
            </Col>
          </>
        ) : null}
      </Row>

      <Row className="row-cols-1 row-cols-xl-2 g-3 mb-3">
        <Col>
          <ChartCard
            title={t('investments:charts.byType')}
            isLoading={summaryQuery.isPending}
            isError={summaryQuery.isError}
            onRetry={() => void summaryQuery.refetch()}
            isEmpty={!summaryQuery.isPending && byTypeItems.length === 0}
            emptyMessage={t('investments:charts.emptyMessage')}
          >
            <CategoryBarChart items={byTypeItems} />
          </ChartCard>
        </Col>
        <Col>
          <ChartCard
            title={t('investments:charts.byIssuer')}
            isLoading={summaryQuery.isPending}
            isError={summaryQuery.isError}
            onRetry={() => void summaryQuery.refetch()}
            isEmpty={!summaryQuery.isPending && byIssuerItems.length === 0}
            emptyMessage={t('investments:charts.emptyMessage')}
          >
            <CategoryBarChart items={byIssuerItems} />
          </ChartCard>
        </Col>
      </Row>

      {positionsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('investments:loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void positionsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!positionsQuery.isError && (
        <div className="table-responsive">
          <Table>
            <thead>
              <tr>
                <th>{t('investments:table.name')}</th>
                <th>{t('investments:table.institution')}</th>
                <th>{t('investments:table.type')}</th>
                <th>{t('investments:table.value')}</th>
                <th>{t('investments:table.invested')}</th>
                <th>{t('investments:table.gain')}</th>
                <th>{t('investments:table.dueDate')}</th>
              </tr>
            </thead>
            <tbody>
              {positionsQuery.isPending &&
                Array.from({ length: 4 }).map((_, index) => <RowSkeleton key={index} />)}

              {!positionsQuery.isPending &&
                positionItems.map((item) => <PositionRow key={item.id} item={item} />)}
            </tbody>
          </Table>
        </div>
      )}
    </div>
  )
}
