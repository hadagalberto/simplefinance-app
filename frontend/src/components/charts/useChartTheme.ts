import { useMemo, useSyncExternalStore } from 'react'

/**
 * Chart colors from the authored `--sf-*` tokens (src/styles/_tokens.scss),
 * for the current color mode.
 *
 * The mode comes from `<html data-bs-theme>` itself, through a
 * MutationObserver, instead of the layout context. The layout context writes
 * the attribute in an effect after its render, so reading CSS variables in
 * that same render would still return the previous mode's values. The
 * observer fires after the attribute has changed, so every read here is for
 * the new mode and charts recolor on a live theme switch without a reload.
 */

export type ChartThemeMode = 'light' | 'dark'

export interface ChartTheme {
  mode: ChartThemeMode
  primary: string
  text: string
  grid: string
  gray: string
  income: string
  expense: string
  palette: string[]
}

function subscribe(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] })
  return () => observer.disconnect()
}

function getSnapshot(): ChartThemeMode {
  // Dark is the boot default, so anything other than an explicit light is dark.
  return document.documentElement.getAttribute('data-bs-theme') === 'light' ? 'light' : 'dark'
}

function getServerSnapshot(): ChartThemeMode {
  return 'dark'
}

export function useChartTheme(): ChartTheme {
  const mode = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot)

  return useMemo(() => {
    const style = getComputedStyle(document.documentElement)
    const read = (name: string) => style.getPropertyValue(name).trim()
    return {
      mode,
      primary: read('--sf-chart-primary'),
      text: read('--sf-chart-text'),
      grid: read('--sf-chart-grid'),
      gray: read('--sf-chart-gray'),
      income: read('--sf-income'),
      expense: read('--sf-expense'),
      palette: [1, 2, 3, 4, 5, 6].map((n) => read(`--sf-chart-${n}`)),
    }
  }, [mode])
}
