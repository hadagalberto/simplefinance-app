import { useState } from 'react'
import { Alert, Badge, Button, Card, CardBody, Form, Placeholder } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { useCreateCategoryRuleMutation } from '@/features/rules/api'

import {
  useAcceptSuggestion,
  useBulkAcceptSuggestions,
  useRejectSuggestion,
  useRunCategorization,
  useSuggestions,
  type SuggestionResponse,
} from './api'

// How long to wait before refetching the queue after enqueuing a backlog run --
// the job runs async on the server, so an immediate refetch would see nothing.
const RUN_REFETCH_DELAY_MS = 3000

/** A recently-accepted suggestion that carried a merchant -- offered as a one-click rule. */
interface RuleOffer {
  suggestionId: string
  merchantName: string
  categoryId: string
  categoryName: string | null
}

function confidenceVariant(confidence: number): string {
  if (confidence >= 0.85) return 'success'
  if (confidence >= 0.6) return 'warning'
  return 'secondary'
}

function TableSkeleton() {
  return (
    <div className="d-flex flex-column gap-2">
      {Array.from({ length: 6 }).map((_, i) => (
        <Placeholder key={i} as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 32 }} />
        </Placeholder>
      ))}
    </div>
  )
}

/**
 * AI-CAT-07: the "Sugestões da IA" queue. Lists pending AI suggestions
 * (transaction + suggested category + confidence + rationale) with per-row
 * accept/reject, multi-select bulk-accept, and an on-demand "categorizar
 * faltantes" backlog run. Accepting a suggestion that carries a merchant surfaces
 * a one-click "criar regra deste merchant" that reuses the existing rules engine
 * (useCreateCategoryRuleMutation -> MERCHANT/CONTAINS -> suggestedCategoryId).
 * All mutations toast success/error and invalidate the queue + transactions feed
 * so the UI reflects server truth (T-16-16). Copies AiSettingsPage's conventions.
 */
export function SuggestionsTab() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const suggestionsQuery = useSuggestions(token)
  const acceptMutation = useAcceptSuggestion()
  const rejectMutation = useRejectSuggestion()
  const bulkAcceptMutation = useBulkAcceptSuggestions()
  const runMutation = useRunCategorization()
  const createRuleMutation = useCreateCategoryRuleMutation()

  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [busyId, setBusyId] = useState<string | null>(null)
  const [ruleOffers, setRuleOffers] = useState<RuleOffer[]>([])

  const suggestions = suggestionsQuery.data ?? []
  const allSelected = suggestions.length > 0 && suggestions.every((s) => selectedIds.has(s.id))

  function toggleSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function toggleSelectAll() {
    setSelectedIds(allSelected ? new Set() : new Set(suggestions.map((s) => s.id)))
  }

  function offerRule(suggestion: SuggestionResponse) {
    if (!suggestion.merchantName || !suggestion.suggestedCategoryId) return
    const offer: RuleOffer = {
      suggestionId: suggestion.id,
      merchantName: suggestion.merchantName,
      categoryId: suggestion.suggestedCategoryId,
      categoryName: suggestion.suggestedCategoryName,
    }
    setRuleOffers((prev) => (prev.some((o) => o.suggestionId === offer.suggestionId) ? prev : [...prev, offer]))
  }

  async function handleAccept(suggestion: SuggestionResponse) {
    setBusyId(suggestion.id)
    try {
      await acceptMutation.mutateAsync({ accessToken: token, id: suggestion.id })
      showNotification({ message: t('suggestions.acceptSuccess'), variant: 'success' })
      offerRule(suggestion)
    } catch {
      showNotification({ message: t('suggestions.acceptError'), variant: 'danger' })
      void suggestionsQuery.refetch()
    } finally {
      setBusyId(null)
    }
  }

  async function handleReject(id: string) {
    setBusyId(id)
    try {
      await rejectMutation.mutateAsync({ accessToken: token, id })
      showNotification({ message: t('suggestions.rejectSuccess'), variant: 'success' })
    } catch {
      showNotification({ message: t('suggestions.rejectError'), variant: 'danger' })
      void suggestionsQuery.refetch()
    } finally {
      setBusyId(null)
    }
  }

  async function handleBulkAccept() {
    const ids = [...selectedIds]
    if (ids.length === 0) {
      showNotification({ message: t('suggestions.selectFirst'), variant: 'warning' })
      return
    }
    try {
      const result = await bulkAcceptMutation.mutateAsync({ accessToken: token, ids })
      showNotification({
        message: t('suggestions.bulkAcceptSuccess', { count: result.accepted }),
        variant: 'success',
      })
      setSelectedIds(new Set())
    } catch {
      showNotification({ message: t('suggestions.bulkAcceptError'), variant: 'danger' })
      void suggestionsQuery.refetch()
    }
  }

  async function handleRun() {
    try {
      await runMutation.mutateAsync({ accessToken: token })
      showNotification({ message: t('suggestions.runEnqueued'), variant: 'success' })
      window.setTimeout(() => void suggestionsQuery.refetch(), RUN_REFETCH_DELAY_MS)
    } catch {
      showNotification({ message: t('suggestions.runError'), variant: 'danger' })
    }
  }

  async function handleCreateRule(offer: RuleOffer) {
    try {
      await createRuleMutation.mutateAsync({
        accessToken: token,
        matchField: 'MERCHANT',
        matchOperator: 'CONTAINS',
        matchValue: offer.merchantName,
        categoryId: offer.categoryId,
        isActive: true,
      })
      showNotification({
        message: t('suggestions.ruleCreated', { merchant: offer.merchantName }),
        variant: 'success',
      })
      setRuleOffers((prev) => prev.filter((o) => o.suggestionId !== offer.suggestionId))
    } catch {
      showNotification({ message: t('suggestions.ruleError'), variant: 'danger' })
    }
  }

  return (
    <Card>
      <CardBody>
        <div className="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
          <div>
            <h5 className="mb-1">{t('suggestions.title')}</h5>
            <p className="text-muted fs-xs mb-0">{t('suggestions.subtitle')}</p>
          </div>
          <div className="d-flex align-items-center gap-2">
            <Button
              variant="outline-primary"
              size="sm"
              onClick={() => void handleBulkAccept()}
              disabled={bulkAcceptMutation.isPending || selectedIds.size === 0}
            >
              {t('suggestions.bulkAccept')}
              {selectedIds.size > 0 && <Badge bg="primary" className="ms-1">{selectedIds.size}</Badge>}
            </Button>
            <Button variant="primary" size="sm" onClick={() => void handleRun()} disabled={runMutation.isPending}>
              {runMutation.isPending ? t('suggestions.running') : t('suggestions.run')}
            </Button>
          </div>
        </div>

        {ruleOffers.map((offer) => (
          <Alert
            key={offer.suggestionId}
            variant="light"
            dismissible
            onClose={() => setRuleOffers((prev) => prev.filter((o) => o.suggestionId !== offer.suggestionId))}
            className="d-flex align-items-center justify-content-between gap-3 py-2"
          >
            <span className="fs-sm">
              {offer.merchantName}
              {offer.categoryName && <span className="text-muted"> → {offer.categoryName}</span>}
            </span>
            <Button
              variant="outline-secondary"
              size="sm"
              onClick={() => void handleCreateRule(offer)}
              disabled={createRuleMutation.isPending}
            >
              <Icon icon="plus" className="me-1" />
              {t('suggestions.createRule')}
            </Button>
          </Alert>
        ))}

        {suggestionsQuery.isError && (
          <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
            <span>{t('suggestions.loadError')}</span>
            <Button variant="outline-danger" size="sm" onClick={() => void suggestionsQuery.refetch()}>
              {t('retry')}
            </Button>
          </Alert>
        )}

        {suggestionsQuery.isPending && <TableSkeleton />}

        {!suggestionsQuery.isPending && !suggestionsQuery.isError && suggestions.length === 0 && (
          <div className="text-center py-5">
            <Icon icon="sparkles" className="fs-1 text-muted mb-3" />
            <h5>{t('suggestions.empty')}</h5>
            <p className="text-muted mb-0">{t('suggestions.emptyHint')}</p>
          </div>
        )}

        {!suggestionsQuery.isPending && !suggestionsQuery.isError && suggestions.length > 0 && (
          <div className="table-responsive">
            <table className="table table-custom table-centered w-100 mb-0">
              <thead className="bg-light bg-opacity-25 thead-sm align-middle">
                <tr className="text-uppercase fs-xxs">
                  <th style={{ width: 36 }}>
                    <Form.Check
                      type="checkbox"
                      checked={allSelected}
                      onChange={toggleSelectAll}
                      aria-label={t('suggestions.selectAll')}
                    />
                  </th>
                  <th>{t('suggestions.columns.transaction')}</th>
                  <th>{t('suggestions.columns.suggestedCategory')}</th>
                  <th>{t('suggestions.columns.confidence')}</th>
                  <th>{t('suggestions.columns.rationale')}</th>
                  <th className="text-end" />
                </tr>
              </thead>
              <tbody>
                {suggestions.map((s) => (
                  <tr key={s.id}>
                    <td>
                      <Form.Check
                        type="checkbox"
                        checked={selectedIds.has(s.id)}
                        onChange={() => toggleSelected(s.id)}
                        aria-label={s.transactionDescription}
                      />
                    </td>
                    <td>
                      <div className="fw-medium">{s.transactionDescription}</div>
                      {s.merchantName && <div className="text-muted fs-xs">{s.merchantName}</div>}
                    </td>
                    <td>{s.suggestedCategoryName ?? <span className="text-muted">{t('suggestions.noCategory')}</span>}</td>
                    <td>
                      <Badge bg={confidenceVariant(s.confidence)}>{Math.round(s.confidence * 100)}%</Badge>
                    </td>
                    <td className="text-muted fs-xs" style={{ maxWidth: 240 }}>
                      {s.rationale ?? t('suggestions.noRationale')}
                    </td>
                    <td>
                      <div className="d-flex justify-content-end gap-1">
                        <Button
                          variant="outline-success"
                          size="sm"
                          onClick={() => void handleAccept(s)}
                          disabled={busyId === s.id}
                        >
                          {t('suggestions.accept')}
                        </Button>
                        <Button
                          variant="outline-danger"
                          size="sm"
                          onClick={() => void handleReject(s.id)}
                          disabled={busyId === s.id}
                        >
                          {t('suggestions.reject')}
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardBody>
    </Card>
  )
}
