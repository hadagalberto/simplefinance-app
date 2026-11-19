// Dashboard on the seeded throwaway database, in the default dark mode and pt-BR.
import { test, expect } from '../support/fixtures'

const TILE_LABELS = ['Patrimônio líquido', 'Saldo total', 'Dívida no cartão', 'Receitas do mês', 'Despesas do mês', 'Saldo do mês']
const CHART_TITLES = ['Gastos por categoria', 'Patrimônio líquido ao longo do tempo', 'Receitas x despesas']

test('dashboard renders dark in pt-BR with its stat tiles and charts', async ({ appPage: page }) => {
  await page.goto('/')
  await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark')
  await expect(page.getByRole('heading', { name: 'Painel', exact: true })).toBeVisible()

  // A stat tile is a card whose body starts with one of the labels above, followed by the amount.
  const tiles = page.locator('.card-body').filter({
    has: page.locator('p.text-muted', { hasText: new RegExp(`^(${TILE_LABELS.join('|')})$`) }),
  })
  await expect.poll(async () => tiles.filter({ visible: true }).count()).toBeGreaterThanOrEqual(4)

  // At least one tile shows a non-zero BRL amount ("R$" then some digit other than 0).
  await expect.poll(async () => tiles.filter({ hasText: /R\$\s*[\d.,]*[1-9]/ }).count()).toBeGreaterThanOrEqual(1)

  // Each chart card holds an ApexCharts svg with at least one drawn series path.
  for (const title of CHART_TITLES) {
    const card = page.locator('.card').filter({ has: page.getByRole('heading', { name: title, exact: true }) })
    await expect(card.locator('svg.apexcharts-svg'), `${title}: chart svg`).toBeVisible()
    const drawn = card.locator('.apexcharts-series path').filter({ visible: true })
    await expect
      .poll(async () => drawn.evaluateAll((paths) => paths.filter((p) => (p.getAttribute('d') ?? '').length > 10).length), {
        message: `${title}: drawn series`,
      })
      .toBeGreaterThanOrEqual(1)
  }
})
