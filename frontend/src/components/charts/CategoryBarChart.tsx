import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const currencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
  maximumFractionDigits: 0,
})

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

/**
 * Generalized distribution item: originally category-only in v1, widened so
 * this one component also serves Investments' by-type/by-issuer
 * distributions (Plan 12-04).
 */
export interface CategoryBarChartItem {
  id: string | null
  label: string
  color: string | null
  amount: number
  percent: number
}

export interface CategoryBarChartProps {
  items: CategoryBarChartItem[]
  onBarClick?: (item: CategoryBarChartItem) => void
  height?: number
}

/**
 * Horizontal distributed bar chart — each bar's fill is the item's own
 * stored color (falls back to the neutral muted `chart-gray` token for
 * uncategorized/no-color items). Axis, grid, label and tooltip
 * colors follow the current color mode through useChartTheme. Bars are clickable for drill-through.
 */
export function CategoryBarChart({ items, onBarClick, height = 280 }: CategoryBarChartProps) {
  const theme = useChartTheme()
  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'bar',
      foreColor: theme.text,
      toolbar: { show: false },
      // Only set `events` when there is a handler: an explicit `events: undefined`
      // replaces ApexCharts' default events object, and its render then throws
      // reading `events.beforeMount` (the investments charts pass no handler).
      ...(onBarClick && {
        events: {
          dataPointSelection: (_e, _ctx, config) => {
            const item = items[config?.dataPointIndex ?? -1]
            if (item) onBarClick(item)
          },
        },
      }),
    },
    plotOptions: {
      bar: {
        horizontal: true,
        distributed: true,
        borderRadius: 4,
      },
    },
    states: {
      hover: {
        filter: { type: onBarClick ? 'lighten' : 'none' },
      },
    },
    colors: items.map((item) => item.color ?? theme.gray),
    dataLabels: {
      enabled: true,
      formatter: (val, opts) => {
        const item = items[opts?.dataPointIndex ?? -1]
        return `${currencyFormatter.format(Number(val))} (${Math.round(item?.percent ?? 0)}%)`
      },
      style: { colors: [theme.text] },
    },
    xaxis: {
      categories: items.map((item) => item.label),
      labels: { style: { colors: theme.text } },
      axisBorder: { color: theme.grid },
      axisTicks: { color: theme.grid },
    },
    yaxis: {
      labels: { style: { colors: theme.text } },
    },
    grid: { borderColor: theme.grid },
    legend: { show: false },
    tooltip: {
      theme: theme.mode,
      y: { formatter: (val) => tooltipCurrencyFormatter.format(Number(val)) },
    },
  })

  return <ThemedApexChart type="bar" height={height} series={[{ data: items.map((item) => item.amount) }]} getOptions={getOptions} />
}
