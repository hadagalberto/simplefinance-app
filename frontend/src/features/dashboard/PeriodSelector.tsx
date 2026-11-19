import { useState } from 'react'
import { Button, Dropdown, DropdownItem, DropdownMenu, DropdownToggle, Form } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

export type PeriodLabel = 'thisMonth' | 'lastMonth' | 'customRange'

export interface DashboardPeriod {
  dateFrom: string
  dateTo: string
  label: PeriodLabel
}

export interface PeriodSelectorProps {
  value: DashboardPeriod
  onChange: (next: DashboardPeriod) => void
}

/** Mirrors TransactionsPage.tsx's `toIsoDate` — kept local since it's a single 4-line function. frontend2 has no date-fns dependency (not pre-approved). */
function toIsoDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function thisMonthPeriod(): DashboardPeriod {
  const now = new Date()
  return {
    dateFrom: toIsoDate(new Date(now.getFullYear(), now.getMonth(), 1)),
    dateTo: toIsoDate(new Date(now.getFullYear(), now.getMonth() + 1, 0)),
    label: 'thisMonth',
  }
}

function lastMonthPeriod(): DashboardPeriod {
  const now = new Date()
  return {
    dateFrom: toIsoDate(new Date(now.getFullYear(), now.getMonth() - 1, 1)),
    dateTo: toIsoDate(new Date(now.getFullYear(), now.getMonth(), 0)),
    label: 'lastMonth',
  }
}

/**
 * Dashboard period picker: This month / Last month / Custom range (inline
 * date inputs), matching TransactionsPage.tsx's Dropdown/date-input
 * conventions. No date-fns dependency — plain `Date` arithmetic only.
 */
export function PeriodSelector({ value, onChange }: PeriodSelectorProps) {
  const { t } = useTranslation('dashboard')
  const [showCustom, setShowCustom] = useState(false)
  const [dateFromDraft, setDateFromDraft] = useState(value.dateFrom)
  const [dateToDraft, setDateToDraft] = useState(value.dateTo)

  function openCustom() {
    setDateFromDraft(value.dateFrom)
    setDateToDraft(value.dateTo)
    setShowCustom(true)
  }

  function applyCustom() {
    if (!dateFromDraft || !dateToDraft) return
    onChange({ dateFrom: dateFromDraft, dateTo: dateToDraft, label: 'customRange' })
    setShowCustom(false)
  }

  return (
    <Dropdown onToggle={(next) => !next && setShowCustom(false)}>
      <DropdownToggle variant="outline-secondary" className="d-flex align-items-center gap-2">
        <Icon icon="calendar" />
        {t(`period.${value.label}`)}
      </DropdownToggle>
      <DropdownMenu align="end">
        {showCustom ? (
          <div className="px-3 py-2" style={{ minWidth: 220 }} onClick={(event) => event.stopPropagation()}>
            <Form.Label className="fs-xs text-muted mb-1">{t('period.from')}</Form.Label>
            <Form.Control
              type="date"
              className="mb-2"
              value={dateFromDraft}
              onChange={(event) => setDateFromDraft(event.target.value)}
            />
            <Form.Label className="fs-xs text-muted mb-1">{t('period.to')}</Form.Label>
            <Form.Control
              type="date"
              className="mb-3"
              value={dateToDraft}
              onChange={(event) => setDateToDraft(event.target.value)}
            />
            <Button size="sm" variant="primary" className="w-100" onClick={applyCustom}>
              {t('period.apply')}
            </Button>
          </div>
        ) : (
          <>
            <DropdownItem
              onClick={() => {
                onChange(thisMonthPeriod())
              }}
            >
              {t('period.thisMonth')}
            </DropdownItem>
            <DropdownItem
              onClick={() => {
                onChange(lastMonthPeriod())
              }}
            >
              {t('period.lastMonth')}
            </DropdownItem>
            <DropdownItem
              onClick={(event) => {
                event.preventDefault()
                openCustom()
              }}
            >
              {t('period.customRange')}
            </DropdownItem>
          </>
        )}
      </DropdownMenu>
    </Dropdown>
  )
}
