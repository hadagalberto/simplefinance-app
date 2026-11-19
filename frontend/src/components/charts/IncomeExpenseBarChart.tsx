import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const monthFormatter = new Intl.DateTimeFormat('pt-BR', { month: 'short' })

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

export interface IncomeExpenseMonthItem {
  year: number
  month: number
  income: number
  expense: number
}

export interface IncomeExpenseBarChartProps {
  items: IncomeExpenseMonthItem[]
  height?: number
}

function Legend() {
  return (
    <div className="d-flex align-items-center justify-content-center gap-4 mb-2 small fw-semibold">
      <span className="d-flex align-items-center gap-1">
        <span className="rounded-circle" style={{ width: 10, height: 10, backgroundColor: 'var(--sf-income)' }} aria-hidden="true" />
        Receitas
      </span>
      <span className="d-flex align-items-center gap-1">
        <span className="rounded-circle" style={{ width: 10, height: 10, backgroundColor: 'var(--sf-expense)' }} aria-hidden="true" />
        Despesas
      </span>
    </div>
  )
}

/**
 * Grouped income/expense bar chart. A bespoke legend row above the chart
 * pairs a colored swatch with a text label per the never-color-only UI
 * convention — the ApexCharts legend itself stays hidden.
 */
export function IncomeExpenseBarChart({ items, height = 280 }: IncomeExpenseBarChartProps) {
  const theme = useChartTheme()
  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'bar',
      foreColor: theme.text,
      toolbar: { show: false },
      stacked: false,
    },
    plotOptions: {
      bar: {
        columnWidth: '55%',
        borderRadius: 4,
      },
    },
    colors: [theme.income, theme.expense],
    dataLabels: { enabled: false },
    xaxis: {
      categories: items.map((item) => monthFormatter.format(new Date(item.year, item.month - 1, 1))),
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

  return (
    <div className="h-100 d-flex flex-column">
      <Legend />
      <div className="flex-grow-1">
        <ThemedApexChart
          type="bar"
          height={height}
          series={[
            { name: 'income', data: items.map((item) => item.income) },
            { name: 'expense', data: items.map((item) => item.expense) },
          ]}
          getOptions={getOptions}
        />
      </div>
    </div>
  )
}
