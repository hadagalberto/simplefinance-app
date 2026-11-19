import type { ApexOptions } from 'apexcharts'

import { MoneyText } from '@/components/MoneyText'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

const tickDateFormatter = new Intl.DateTimeFormat('pt-BR', { month: 'short', day: '2-digit' })

/**
 * X-axis tick text for one point date. ApexCharts maps category charts to a
 * numeric axis and also calls the label formatter for positions that are not
 * one of the dates (it then passes undefined), so anything that is not a date
 * string is returned as plain text instead of throwing from Intl.
 */
function formatTickDate(value: string | number | undefined): string {
  if (typeof value !== 'string') return value === undefined ? '' : String(value)
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : tickDateFormatter.format(date)
}

export interface EvolutionPoint {
  date: string
  value: number
}

export interface EvolutionLineChartProps {
  points: EvolutionPoint[]
  valueLabel?: string
  height?: number
}

function formatCompact(value: number): string {
  if (Math.abs(value) >= 1000) {
    return `R$ ${Math.round(value / 1000)}k`
  }
  return new Intl.NumberFormat('pt-BR', {
    style: 'currency',
    currency: 'BRL',
    maximumFractionDigits: 0,
  }).format(value)
}

function SinglePointView({ point }: { point: EvolutionPoint }) {
  return (
    <div className="d-flex flex-column align-items-center justify-content-center gap-3 h-100" style={{ minHeight: 180 }}>
      <div className="d-flex align-items-center gap-3" style={{ width: '100%', maxWidth: 280 }}>
        <span className="flex-grow-1" style={{ borderTop: '1px dashed var(--sf-chart-grid)' }} />
        <span
          className="rounded-circle flex-shrink-0"
          style={{ width: 12, height: 12, backgroundColor: 'var(--sf-chart-primary)' }}
          aria-hidden="true"
        />
      </div>
      <MoneyText amount={point.value} variant="neutral" showSign={false} className="fs-4 fw-semibold" />
      <p className="text-muted small text-center mb-0" style={{ maxWidth: 280 }}>
        O histórico começa hoje. Volte amanhã para ver a evolução.
      </p>
    </div>
  )
}

/**
 * Single-series evolution line chart (net worth, investment value, etc).
 * This component is NEVER called with `points.length === 0` — the parent
 * ChartCard's `isEmpty` prop owns the 0-point case entirely and never mounts
 * this component, exactly mirroring v1's NetWorthLineChart contract. When
 * `points.length === 1`, a 1-point ApexCharts line looks broken (a single
 * dot with no line), so that case renders a bespoke SinglePointView instead
 * of mounting ThemedApexChart.
 */
export function EvolutionLineChart({ points, valueLabel = 'Value', height = 280 }: EvolutionLineChartProps) {
  // Called before the early return so the hook order never changes.
  const theme = useChartTheme()

  if (points.length === 1) {
    return <SinglePointView point={points[0]} />
  }

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'line',
      foreColor: theme.text,
      toolbar: { show: false },
      zoom: { enabled: false },
    },
    stroke: {
      curve: 'smooth',
      width: 2,
      colors: [theme.primary],
    },
    markers: {
      size: 3,
      colors: [theme.primary],
    },
    xaxis: {
      categories: points.map((point) => point.date),
      labels: {
        style: { colors: theme.text },
        formatter: (val) => formatTickDate(val),
      },
      axisBorder: { color: theme.grid },
      axisTicks: { color: theme.grid },
    },
    yaxis: {
      labels: {
        style: { colors: theme.text },
        formatter: (val) => formatCompact(val),
      },
    },
    grid: { borderColor: theme.grid },
    colors: [theme.primary],
    tooltip: {
      theme: theme.mode,
      y: { formatter: (val) => tooltipCurrencyFormatter.format(Number(val)) },
    },
  })

  return <ThemedApexChart type="line" height={height} series={[{ name: valueLabel, data: points.map((point) => point.value) }]} getOptions={getOptions} />
}
