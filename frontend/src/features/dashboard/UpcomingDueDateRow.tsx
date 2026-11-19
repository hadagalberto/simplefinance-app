import { Badge } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { MoneyText } from '@/components/MoneyText'
import type { UpcomingDueDateItem } from '@/features/dashboard/api'

export interface UpcomingDueDateRowProps {
  item: UpcomingDueDateItem
}

function formatShortDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit' }).format(new Date(dateString))
}

/** Row: card icon, institution/account name, due date, statement balance, days-until badge. */
export function UpcomingDueDateRow({ item }: UpcomingDueDateRowProps) {
  const { t } = useTranslation('dashboard')
  const daysUntil = Math.ceil((new Date(item.dueDate).getTime() - Date.now()) / 86400000)

  return (
    <div className="d-flex align-items-center justify-content-between gap-3 border-bottom py-3">
      <div className="d-flex align-items-center gap-3">
        <Icon icon="credit-card" className="text-muted" />
        <div>
          <p className="fw-semibold mb-0">{item.institutionName ?? item.accountName}</p>
          <p className="text-muted small mb-0">{t('upcomingDueDate.due', { date: formatShortDate(item.dueDate) })}</p>
        </div>
      </div>
      <div className="d-flex align-items-center gap-3">
        {item.statementBalance != null && (
          <MoneyText amount={item.statementBalance} variant="expense" showSign={false} />
        )}
        <Badge bg="secondary">
          {daysUntil <= 0 ? t('upcomingDueDate.dueToday') : t('upcomingDueDate.daysUntil', { count: daysUntil })}
        </Badge>
      </div>
    </div>
  )
}
