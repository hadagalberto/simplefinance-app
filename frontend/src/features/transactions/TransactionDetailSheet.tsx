/**
 * Side panel with one transaction's details: amount, date, description,
 * merchant and account, plus category editing, a note, and the hide / transfer
 * actions. Opened from TransactionsPage by a row click or the row menu's
 * "View details" / "Recategorize" items.
 *
 * Changing the category can also apply it to similar transactions, using the
 * similar-count query to say how many would change. The panel is rendered
 * once at the bottom of TransactionsPage and opens while `transactionId` is
 * set, rather than one panel per row.
 */

import { useEffect, useState } from 'react'
import Select from 'react-select'
import { useTranslation } from 'react-i18next'
import { Alert, Badge, Button, Form, Offcanvas, Placeholder } from 'react-bootstrap'

import { MoneyText } from '@/components/MoneyText'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import {
  useApplySimilarMutation,
  useRecategorizeMutation,
  useSimilarCountQuery,
  useToggleHiddenMutation,
  useToggleTransferMutation,
  useTransactionDetailQuery,
  useUpdateNoteMutation,
  type Category,
} from '@/features/transactions/api'

function formatDetailDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(
    new Date(dateString)
  )
}

interface RecentCategoryAssignment {
  categoryId: string
  categoryName: string
}

export interface TransactionDetailSheetProps {
  transactionId: string | null
  categories: Category[]
  /** True when opened via the kebab's "Recategorize" action -- auto-opens the category picker. */
  focusCategory?: boolean
  onClose: () => void
}

type SelectOption = { value: string; label: string }

export function TransactionDetailSheet({
  transactionId,
  categories,
  focusCategory = false,
  onClose,
}: TransactionDetailSheetProps) {
  const { t } = useTranslation(['transactions', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const [categoryOpen, setCategoryOpen] = useState(false)
  const [noteDraft, setNoteDraft] = useState('')
  const [recentAssignment, setRecentAssignment] = useState<RecentCategoryAssignment | null>(null)

  const detailQuery = useTransactionDetailQuery(token, transactionId)
  const recategorizeMutation = useRecategorizeMutation()
  const updateNoteMutation = useUpdateNoteMutation()
  const toggleHiddenMutation = useToggleHiddenMutation()
  const toggleTransferMutation = useToggleTransferMutation()
  const applySimilarMutation = useApplySimilarMutation()

  const similarQuery = useSimilarCountQuery(token, transactionId, recentAssignment?.categoryId ?? null)

  const detail = detailQuery.data

  // Reset transient/local state whenever a different transaction is opened.
  useEffect(() => {
    setRecentAssignment(null)
    setCategoryOpen(false)
  }, [transactionId])

  useEffect(() => {
    if (detail) setNoteDraft(detail.notes ?? '')
  }, [detail])

  useEffect(() => {
    if (focusCategory && detail) setCategoryOpen(true)
  }, [focusCategory, detail])

  async function handleCategorySelect(categoryId: string | null, categoryName: string | null) {
    if (!transactionId) return
    try {
      await recategorizeMutation.mutateAsync({ accessToken: token, id: transactionId, categoryId })
      setCategoryOpen(false)
      if (categoryId && categoryName) {
        setRecentAssignment({ categoryId, categoryName })
      } else {
        setRecentAssignment(null)
      }
    } catch {
      showNotification({ message: t('errors.saveDetail'), variant: 'danger' })
    }
  }

  async function handleNoteBlur() {
    if (!transactionId || !detail) return
    if (noteDraft === (detail.notes ?? '')) return
    try {
      await updateNoteMutation.mutateAsync({ accessToken: token, id: transactionId, note: noteDraft || null })
    } catch {
      showNotification({ message: t('errors.saveDetail'), variant: 'danger' })
    }
  }

  async function handleToggleHidden(checked: boolean) {
    if (!transactionId) return
    try {
      await toggleHiddenMutation.mutateAsync({ accessToken: token, id: transactionId, isHidden: checked })
    } catch {
      showNotification({ message: t('errors.saveDetail'), variant: 'danger' })
    }
  }

  async function handleToggleTransfer(checked: boolean) {
    if (!transactionId) return
    try {
      await toggleTransferMutation.mutateAsync({ accessToken: token, id: transactionId, isTransfer: checked })
    } catch {
      showNotification({ message: t('errors.saveDetail'), variant: 'danger' })
    }
  }

  async function handleApplySimilar() {
    if (!transactionId || !recentAssignment) return
    try {
      const result = await applySimilarMutation.mutateAsync({
        accessToken: token,
        id: transactionId,
        categoryId: recentAssignment.categoryId,
      })
      showNotification({
        message: t('detail.applySimilarSuccess', {
          count: result.updatedCount,
          category: recentAssignment.categoryName,
        }),
        variant: 'success',
      })
      setRecentAssignment(null)
    } catch {
      showNotification({ message: t('errors.saveDetail'), variant: 'danger' })
    }
  }

  const amountVariant = detail
    ? detail.isTransfer
      ? 'transfer'
      : detail.type === 'Credit'
        ? 'income'
        : 'expense'
    : 'neutral'

  const categoryOptions: SelectOption[] = categories.map((category) => ({
    value: category.id,
    label: category.name,
  }))
  const selectedCategoryOption = detail?.categoryId
    ? (categoryOptions.find((option) => option.value === detail.categoryId) ?? {
        value: detail.categoryId,
        label: detail.categoryName ?? '',
      })
    : null

  return (
    <Offcanvas
      show={!!transactionId}
      onHide={onClose}
      placement="end"
      className="mw-100"
      style={{ width: 420 }}
    >
      <Offcanvas.Header closeButton className="border-bottom">
        <Offcanvas.Title>{t('detail.title')}</Offcanvas.Title>
      </Offcanvas.Header>
      <Offcanvas.Body>
        {detailQuery.isLoading && (
          <div className="d-flex flex-column gap-3">
            <Placeholder as="div" animation="glow">
              <Placeholder xs={6} size="lg" />
            </Placeholder>
            <Placeholder as="div" animation="glow">
              <Placeholder xs={12} />
            </Placeholder>
            <Placeholder as="div" animation="glow">
              <Placeholder xs={12} />
            </Placeholder>
            <Placeholder as="div" animation="glow">
              <Placeholder xs={12} />
            </Placeholder>
          </div>
        )}

        {detailQuery.isError && (
          <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
            <span>{t('errors.loadDetail')}</span>
            <Button variant="outline-danger" size="sm" onClick={() => void detailQuery.refetch()}>
              {t('common:actions.retry')}
            </Button>
          </Alert>
        )}

        {detail && (
          <div className="d-flex flex-column gap-4">
            <div>
              <MoneyText amount={detail.amount} variant={amountVariant} className="fs-24 fw-semibold d-block" />
              <p className="fs-xs fw-semibold text-muted mb-0">{formatDetailDate(detail.date)}</p>
            </div>

            <div>
              <Form.Label className="fs-xs fw-semibold text-muted mb-1">{t('detail.originalDescription')}</Form.Label>
              <p className="fs-sm mb-0">{detail.descriptionRaw || detail.description}</p>
            </div>

            <div>
              <Form.Label className="fs-xs fw-semibold text-muted mb-1">{t('detail.merchant')}</Form.Label>
              <p className="fs-sm mb-0">{detail.merchantName ?? '—'}</p>
            </div>

            <div>
              <Form.Label className="fs-xs fw-semibold text-muted mb-1">{t('detail.account')}</Form.Label>
              <p className="fs-sm mb-0">{detail.accountName}</p>
            </div>

            <div>
              <Form.Label className="fs-xs fw-semibold text-muted mb-1">{t('detail.category')}</Form.Label>
              <div className="d-flex align-items-center gap-2">
                <div className="flex-grow-1">
                  <Select<SelectOption>
                    className="react-select"
                    classNamePrefix="react-select"
                    isClearable
                    isDisabled={recategorizeMutation.isPending}
                    placeholder={t('detail.assignCategory')}
                    options={categoryOptions}
                    value={selectedCategoryOption}
                    menuIsOpen={categoryOpen}
                    onMenuOpen={() => setCategoryOpen(true)}
                    onMenuClose={() => setCategoryOpen(false)}
                    onChange={(option) => void handleCategorySelect(option?.value ?? null, option?.label ?? null)}
                  />
                </div>
                {detail.categoryId && (
                  <Badge bg="secondary-subtle" text="secondary">
                    {t(`categorySource.${detail.categorySource}`)}
                  </Badge>
                )}
              </div>

              {recentAssignment && similarQuery.data && (
                <div className="mt-2 rounded border bg-light-subtle p-3">
                  <p className="fs-sm mb-2">
                    {t('detail.applySimilarPrompt', {
                      category: recentAssignment.categoryName,
                      count: similarQuery.data.count,
                      merchant: similarQuery.data.merchant,
                    })}
                  </p>
                  <div className="d-flex gap-2">
                    <Button
                      size="sm"
                      variant="primary"
                      disabled={applySimilarMutation.isPending}
                      onClick={() => void handleApplySimilar()}
                    >
                      {t('detail.applyToSimilar')}
                    </Button>
                    <Button size="sm" variant="link" onClick={() => setRecentAssignment(null)}>
                      {t('detail.noThanks')}
                    </Button>
                  </div>
                </div>
              )}
            </div>

            <div>
              <Form.Label className="fs-xs fw-semibold text-muted mb-1" htmlFor="txn-detail-notes">
                {t('detail.notes')}
              </Form.Label>
              <Form.Control
                as="textarea"
                id="txn-detail-notes"
                rows={3}
                value={noteDraft}
                onChange={(event) => setNoteDraft(event.target.value)}
                onBlur={() => void handleNoteBlur()}
                placeholder={t('detail.notesPlaceholder')}
              />
            </div>

            <hr className="my-0" />

            <div className="d-flex align-items-start justify-content-between gap-3">
              <div>
                <p className="fs-sm fw-medium mb-1">{t('detail.hideFromReports')}</p>
                <p className="fs-xs text-muted mb-0">{t('detail.hideFromReportsDescription')}</p>
              </div>
              <Form.Check
                type="switch"
                id="txn-detail-hidden"
                checked={detail.isHidden}
                disabled={toggleHiddenMutation.isPending}
                onChange={(event) => void handleToggleHidden(event.target.checked)}
                aria-label={t('detail.hideFromReports')}
              />
            </div>

            <div className="d-flex align-items-start justify-content-between gap-3">
              <div>
                <p className="fs-sm fw-medium mb-1">{t('detail.internalTransfer')}</p>
                <p className="fs-xs text-muted mb-0">{t('detail.internalTransferDescription')}</p>
              </div>
              <Form.Check
                type="switch"
                id="txn-detail-transfer"
                checked={detail.isTransfer}
                disabled={toggleTransferMutation.isPending}
                onChange={(event) => void handleToggleTransfer(event.target.checked)}
                aria-label={t('detail.internalTransfer')}
              />
            </div>
          </div>
        )}
      </Offcanvas.Body>
    </Offcanvas>
  )
}
