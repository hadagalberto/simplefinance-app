import { Icon as IconifyIcon } from '@/components/Icon'
import { Link } from 'react-router'
import { Card, CardBody, ProgressBar } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { resolveCategoryIcon } from '@/features/categories/icons'
import type { AtRiskBudgetItem } from '@/features/dashboard/api'

export interface AtRiskBudgetCardProps {
  item: AtRiskBudgetItem
}

const PROGRESS_VARIANT: Record<AtRiskBudgetItem['state'], string> = {
  Ok: 'success',
  Attention: 'warning',
  Exceeded: 'danger',
}

/** Compact card for one at-risk budget: icon+name, current/projected %, mini ProgressBar, "View budget" link. */
export function AtRiskBudgetCard({ item }: AtRiskBudgetCardProps) {
  const { t } = useTranslation('dashboard')

  return (
    <Card>
      <CardBody className="d-flex flex-column gap-3">
        <div className="d-flex align-items-center justify-content-between gap-2">
          <div className="d-flex align-items-center gap-2">
            <IconifyIcon icon={resolveCategoryIcon(item.categoryIcon)} className="text-muted" />
            <span className="fw-semibold">{item.categoryName}</span>
          </div>
          <span className="fw-semibold tabular-nums">{Math.round(item.percentOfCap)}%</span>
        </div>

        <ProgressBar now={Math.min(100, item.percentOfCap)} variant={PROGRESS_VARIANT[item.state]} style={{ height: 6 }} />

        <div className="d-flex align-items-center justify-content-between gap-2">
          <span className="text-muted small tabular-nums">
            {t('atRiskCard.projected', { percent: Math.round(item.projectedPercent) })}
          </span>
          <Link to="/budget" className="fw-semibold small text-decoration-none">
            {t('atRiskCard.viewBudget')}
          </Link>
        </div>
      </CardBody>
    </Card>
  )
}
