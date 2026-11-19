import { useMemo } from 'react'
import type { ApexOptions } from 'apexcharts'
import ReactApexChart, { type Props as ReactApexChartProps } from 'react-apexcharts'

import { useChartTheme } from './useChartTheme'

export interface ThemedApexChartProps {
  /** Chart kind; when omitted, the `chart.type` from the built options is used. */
  type?: ReactApexChartProps['type']
  height?: number | string
  width?: number | string
  /** Builds the options from the current mode's CSS variables; called again on every mode change. */
  getOptions: () => ApexOptions
  series: ApexOptions['series']
  className?: string
}

/**
 * ApexCharts renderer that follows the light/dark color mode.
 *
 * The options are rebuilt whenever the mode changes, and the chart is keyed on
 * the mode so it remounts with the new colors. ApexCharts does not reliably
 * repaint axis, grid and tooltip colors through an in-place options update.
 */
export default function ThemedApexChart({
  type,
  height,
  width = '100%',
  getOptions,
  series,
  className,
}: ThemedApexChartProps) {
  const { mode } = useChartTheme()

  // `mode` is listed on purpose: getOptions reads CSS variables that change with it.
  const options = useMemo(() => getOptions(), [getOptions, mode])

  return (
    <ReactApexChart
      key={mode}
      // apexcharts lists a few chart kinds the React binding's type union lacks; our charts use none of them.
      type={type ?? (options.chart?.type as ThemedApexChartProps['type'])}
      height={height}
      width={width}
      options={options}
      series={series}
      className={className}
    />
  )
}
