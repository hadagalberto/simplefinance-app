import { useState } from 'react'
import { Alert, Button, Card, CardBody, Form, Placeholder, Table } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { useCategoriesQuery } from '@/features/categories/api'

import { BudgetLimitDialog, type BudgetLimitDialogCategory } from './BudgetLimitDialog'
import { BudgetRow } from './BudgetRow'
import { MonthSwitcher } from './MonthSwitcher'
import { useBudgetsQuery, useDeleteBudgetMutation, type BudgetRow as BudgetRowData } from './api'

const monthYearFormatter = new Intl.DateTimeFormat('pt-BR', { month: 'long', year: 'numeric' })

function formatMonthYear(year: number, month: number): string {
  return monthYearFormatter.format(new Date(year, month - 1, 1))
}

function formatPreviousMonthYear(year: number, month: number): string {
  const date = new Date(year, month - 1, 1)
  date.setMonth(date.getMonth() - 1)
  return monthYearFormatter.format(date)
}

interface DialogTarget {
  category: BudgetLimitDialogCategory | null
  initialLimitAmount?: number
}

function RowSkeleton() {
  return (
    <tr>
      {Array.from({ length: 7 }).map((_, index) => (
        <td key={index}>
          <Placeholder as="span" animation="glow">
            <Placeholder xs={8} />
          </Placeholder>
        </td>
      ))}
    </tr>
  )
}

/**
 * FE-BUD-01: the full Budget screen -- global cap card, category budget
 * table (progress/state/projection per row), add-a-budget row,
 * copy-forward banner, and empty state, wired to the unchanged
 * GET/POST/DELETE /api/budgets endpoints from v1.0's Phase 5.
 */
export function BudgetPage() {
  const { t } = useTranslation('budget')
  const { accessToken } = useAuth()
  const { showNotification } = useNotificationContext()
  const token = accessToken ?? ''

  const now = new Date()
  const [year, setYear] = useState(now.getFullYear())
  const [month, setMonth] = useState(now.getMonth() + 1)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [dialogTarget, setDialogTarget] = useState<DialogTarget | null>(null)
  const [addCategoryId, setAddCategoryId] = useState<string>('')
  const [dismissedBannerKey, setDismissedBannerKey] = useState<string | null>(null)

  const budgetsQuery = useBudgetsQuery(token, year, month)
  const categoriesQuery = useCategoriesQuery(token)
  const deleteMutation = useDeleteBudgetMutation()

  const data = budgetsQuery.data
  const items = data?.items ?? []
  const globalCapRow = items.find((item) => item.categoryId === null) ?? null
  const categoryRows = items.filter((item) => item.categoryId !== null)

  const periodKey = `${year}-${month}`
  const showCopyForwardBanner = !!data?.copiedFromPreviousMonth && dismissedBannerKey !== periodKey
  const previousMonthLabel = formatPreviousMonthYear(year, month)

  const budgetedCategoryIds = new Set(categoryRows.map((row) => row.categoryId))
  const unbudgetedCategories = (categoriesQuery.data ?? []).filter(
    (category) => !budgetedCategoryIds.has(category.id)
  )

  const isEmpty = !budgetsQuery.isPending && !budgetsQuery.isError && items.length === 0 && !data?.copiedFromPreviousMonth

  function handleMonthChange(nextYear: number, nextMonth: number) {
    setYear(nextYear)
    setMonth(nextMonth)
  }

  function handleOpenGlobalCapDialog() {
    setDialogTarget({ category: null, initialLimitAmount: globalCapRow?.limitAmount })
    setDialogOpen(true)
  }

  function handleOpenEmptyStateDialog() {
    setDialogTarget({ category: null, initialLimitAmount: undefined })
    setDialogOpen(true)
  }

  function handleEditRow(row: BudgetRowData) {
    if (!row.categoryId) {
      handleOpenGlobalCapDialog()
      return
    }
    setDialogTarget({
      category: {
        id: row.categoryId,
        name: row.categoryName ?? '',
        icon: row.categoryIcon,
        color: row.categoryColor,
      },
      initialLimitAmount: row.limitAmount,
    })
    setDialogOpen(true)
  }

  function handleAddCategoryLimit() {
    const category = (categoriesQuery.data ?? []).find((item) => item.id === addCategoryId)
    if (!category) return
    setDialogTarget({
      category: { id: category.id, name: category.name, icon: category.icon, color: category.color },
      initialLimitAmount: undefined,
    })
    setDialogOpen(true)
    setAddCategoryId('')
  }

  async function handleRemove(row: BudgetRowData) {
    try {
      await deleteMutation.mutateAsync({ accessToken: token, id: row.id })
    } catch {
      showNotification({ message: t('deleteError'), variant: 'danger' })
    }
  }

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-3">
        <h4 className="mb-0">{t('pageTitle')}</h4>
        <MonthSwitcher year={year} month={month} onChange={handleMonthChange} />
      </div>

      {showCopyForwardBanner && (
        <Alert variant="info" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('copyForwardBanner', { month: previousMonthLabel })}</span>
          <Button variant="outline-secondary" size="sm" onClick={() => setDismissedBannerKey(periodKey)}>
            {t('dismiss')}
          </Button>
        </Alert>
      )}

      {budgetsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void budgetsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!budgetsQuery.isError && (
        <Card className="mb-3">
          <CardBody>
            {budgetsQuery.isPending ? (
              <Placeholder as="p" animation="glow" className="mb-0">
                <Placeholder xs={12} style={{ height: 32 }} />
              </Placeholder>
            ) : globalCapRow ? (
              <div className="table-responsive">
                <Table className="mb-0">
                  <tbody>
                    <BudgetRow
                      row={globalCapRow}
                      onEdit={() => handleEditRow(globalCapRow)}
                      onRemove={() => void handleRemove(globalCapRow)}
                    />
                  </tbody>
                </Table>
              </div>
            ) : (
              <div className="d-flex align-items-center justify-content-between gap-3">
                <div>
                  <p className="fw-semibold mb-1">{t('overallCapLabel')}</p>
                  <p className="text-muted mb-0">{t('overallCap.noneSet')}</p>
                </div>
                <Button variant="outline-primary" onClick={handleOpenGlobalCapDialog}>
                  {t('overallCap.setCap')}
                </Button>
              </div>
            )}
          </CardBody>
        </Card>
      )}

      {!budgetsQuery.isError && isEmpty && (
        <Card>
          <CardBody className="text-center py-5">
            <h5>{t('empty.title', { month: formatMonthYear(year, month) })}</h5>
            <p className="text-muted mb-3">{t('empty.subtitle')}</p>
            <Button variant="primary" onClick={handleOpenEmptyStateDialog}>
              {t('setLimit')}
            </Button>
          </CardBody>
        </Card>
      )}

      {!budgetsQuery.isError && !isEmpty && (
        <>
          <div className="table-responsive">
            <Table>
              <thead>
                <tr>
                  <th>{t('table.category')}</th>
                  <th>{t('table.progress')}</th>
                  <th>{t('table.spentOfLimit')}</th>
                  <th>{t('table.remaining')}</th>
                  <th>{t('table.projection')}</th>
                  <th>{t('table.state')}</th>
                  <th>{t('table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {budgetsQuery.isPending &&
                  Array.from({ length: 4 }).map((_, index) => <RowSkeleton key={index} />)}

                {!budgetsQuery.isPending &&
                  categoryRows.map((row) => (
                    <BudgetRow
                      key={row.id}
                      row={row}
                      onEdit={() => handleEditRow(row)}
                      onRemove={() => void handleRemove(row)}
                    />
                  ))}
              </tbody>
            </Table>
          </div>

          {!budgetsQuery.isPending && unbudgetedCategories.length > 0 && (
            <div className="d-flex align-items-center gap-2 mt-3">
              <Form.Select
                value={addCategoryId}
                onChange={(event) => setAddCategoryId(event.target.value)}
                style={{ maxWidth: 280 }}
              >
                <option value="">{t('addCategory.placeholder')}</option>
                {unbudgetedCategories.map((category) => (
                  <option key={category.id} value={category.id}>
                    {category.name}
                  </option>
                ))}
              </Form.Select>
              <Button variant="outline-primary" disabled={!addCategoryId} onClick={handleAddCategoryLimit}>
                {t('setLimit')}
              </Button>
            </div>
          )}
        </>
      )}

      <BudgetLimitDialog
        show={dialogOpen}
        onHide={() => setDialogOpen(false)}
        category={dialogTarget?.category ?? null}
        initialLimitAmount={dialogTarget?.initialLimitAmount}
        year={year}
        month={month}
        onSaved={() => void budgetsQuery.refetch()}
      />
    </div>
  )
}
