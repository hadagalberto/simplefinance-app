import { Icon as IconifyIcon } from '@/components/Icon'
import { Button, ProgressBar } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { MoneyText } from '@/components/MoneyText'
import { resolveCategoryIcon } from '@/features/categories/icons'

import { BudgetStateBadge } from './BudgetStateBadge'
import type { BudgetRow as BudgetRowData } from './api'

export interface BudgetRowProps {
  row: BudgetRowData
  onEdit: () => void
  onRemove: () => void
}

const PROGRESS_VARIANT: Record<BudgetRowData['state'], string> = {
  Ok: 'success',
  Attention: 'warning',
  Exceeded: 'danger',
}

/** One Budget table row (or the global-cap card row): category/progress/spend/remaining/projection/state/actions. */
export function BudgetRow({ row, onEdit, onRemove }: BudgetRowProps) {
  const { t } = useTranslation('budget')
  const iconName = row.categoryId ? resolveCategoryIcon(row.categoryIcon) : 'lucide:wallet'
  const isProjectedToExceed = row.projectedPercent > 100

  return (
    <tr>
      <td>
        <div className="d-flex align-items-center gap-2">
          <IconifyIcon icon={iconName} className="text-muted" />
          {row.categoryId && row.categoryColor && (
            <span
              className="rounded-circle flex-shrink-0"
              style={{ backgroundColor: row.categoryColor, width: 8, height: 8 }}
            />
          )}
          <span className="fw-semibold">{row.categoryId ? row.categoryName : t('overallCapLabel')}</span>
        </div>
      </td>

      <td>
        <div className="d-flex align-items-center gap-2" style={{ minWidth: 140 }}>
          <ProgressBar
            now={Math.min(100, row.percentOfCap)}
            variant={PROGRESS_VARIANT[row.state]}
            className="flex-grow-1"
            style={{ height: 6 }}
          />
          <span className="fw-semibold text-nowrap">{Math.round(row.percentOfCap)}%</span>
        </div>
      </td>

      <td>
        <span className="text-muted">
          <MoneyText amount={row.spentAmount} variant="neutral" showSign={false} className="text-body" />
          {t('table.spentOfSeparator')}
          <MoneyText amount={row.limitAmount} variant="neutral" showSign={false} className="text-body" />
        </span>
      </td>

      <td>
        <MoneyText amount={row.remaining} variant="neutral" showSign={false} />
      </td>

      <td>
        {isProjectedToExceed ? (
          <span className="d-inline-flex align-items-center gap-1 text-danger">
            <IconifyIcon icon="lucide:trending-up" />
            <MoneyText amount={row.projectedSpend} variant="neutral" showSign={false} className="text-danger" />
            {` (${Math.round(row.projectedPercent)}%)`}
          </span>
        ) : (
          <span className="text-muted">
            <MoneyText amount={row.projectedSpend} variant="neutral" showSign={false} />
            {` (${Math.round(row.projectedPercent)}%)`}
          </span>
        )}
      </td>

      <td>
        <BudgetStateBadge state={row.state} />
      </td>

      <td>
        <div className="d-flex align-items-center gap-2">
          <Button variant="outline-secondary" size="sm" onClick={onEdit}>
            {t('row.editLimit')}
          </Button>
          <Button variant="link" size="sm" className="text-danger" onClick={onRemove}>
            {t('row.remove')}
          </Button>
        </div>
      </td>
    </tr>
  )
}
