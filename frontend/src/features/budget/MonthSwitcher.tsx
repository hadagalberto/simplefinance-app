import { Button } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

export interface MonthSwitcherProps {
  year: number
  month: number
  onChange: (year: number, month: number) => void
}

const monthYearFormatter = new Intl.DateTimeFormat('pt-BR', { month: 'long', year: 'numeric' })

/** Prev/next chevron buttons + "{Month Year}" label, mirrors v1's MonthSwitcher.tsx exact layout. */
export function MonthSwitcher({ year, month, onChange }: MonthSwitcherProps) {
  const { t } = useTranslation('budget')

  function handlePrevious() {
    if (month === 1) {
      onChange(year - 1, 12)
    } else {
      onChange(year, month - 1)
    }
  }

  function handleNext() {
    if (month === 12) {
      onChange(year + 1, 1)
    } else {
      onChange(year, month + 1)
    }
  }

  const label = monthYearFormatter.format(new Date(year, month - 1, 1))

  return (
    <div className="d-flex align-items-center gap-2">
      <Button variant="link" size="sm" className="p-0" onClick={handlePrevious} aria-label={t('monthSwitcher.previousMonth')}>
        <Icon icon="chevron-left" />
      </Button>
      <span className="text-center fw-semibold text-capitalize" style={{ minWidth: 140 }}>
        {label}
      </span>
      <Button variant="link" size="sm" className="p-0" onClick={handleNext} aria-label={t('monthSwitcher.nextMonth')}>
        <Icon icon="chevron-right" />
      </Button>
    </div>
  )
}
