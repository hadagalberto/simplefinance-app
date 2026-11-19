import { useState } from 'react'
import { Alert, Button, Card, CardBody } from 'react-bootstrap'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError, apiFetch } from '@/lib/apiClient'

import {
  actionInvalidationKey,
  useCancelActionMutation,
  useConfirmActionMutation,
  type ActionStatusResponse,
} from './api'
import type { StreamActionCard } from './useChatStream'

/**
 * AI-CHAT-06 / AI-CHAT-04: a proposed write-action from an SSE `action` frame.
 * The write NEVER fires automatically (T-17-24) — it only happens when the user
 * clicks Confirmar, which POSTs .../confirm and, on success, invalidates the
 * affected feature query (by actionType, in useConfirmActionMutation). A 409
 * (already decided / expired) and 404 (unknown) are surfaced as friendly toasts.
 * Once decided, the card collapses to a static resolution note.
 */
export function ActionConfirmCard({ action }: { action: StreamActionCard }) {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const confirmMutation = useConfirmActionMutation()
  const cancelMutation = useCancelActionMutation()

  const [resolution, setResolution] = useState<'confirmed' | 'cancelled' | null>(null)

  function reportError(error: unknown, fallbackKey: string) {
    if (error instanceof ApiError && error.status === 409) {
      showNotification({ message: t('assistant.action.alreadyDecided'), variant: 'warning' })
    } else if (error instanceof ApiError && error.status === 404) {
      showNotification({ message: t('assistant.action.notFound'), variant: 'warning' })
    } else {
      showNotification({ message: t(fallbackKey), variant: 'danger' })
    }
  }

  async function handleConfirm() {
    try {
      await confirmMutation.mutateAsync({
        accessToken: token,
        id: action.id,
        actionType: action.actionType,
      })
      showNotification({ message: t('assistant.action.confirmSuccess'), variant: 'success' })
      setResolution('confirmed')
    } catch (error) {
      reportError(error, 'assistant.action.confirmError')
    }
  }

  async function handleCancel() {
    try {
      await cancelMutation.mutateAsync({
        accessToken: token,
        id: action.id,
        actionType: action.actionType,
      })
      showNotification({ message: t('assistant.action.cancelSuccess'), variant: 'success' })
      setResolution('cancelled')
    } catch (error) {
      reportError(error, 'assistant.action.cancelError')
    }
  }

  const busy = confirmMutation.isPending || cancelMutation.isPending

  if (resolution) {
    return (
      <Alert variant={resolution === 'confirmed' ? 'success' : 'secondary'} className="mb-0">
        <Icon icon={resolution === 'confirmed' ? 'check' : 'x'} className="me-1" />
        {resolution === 'confirmed'
          ? t('assistant.action.confirmSuccess')
          : t('assistant.action.cancelSuccess')}
      </Alert>
    )
  }

  return (
    <Card border="warning" className="mb-0">
      <CardBody className="d-flex flex-column gap-2">
        <div className="d-flex align-items-center gap-2">
          <Icon icon="shield-check" className="text-warning" />
          <span className="fw-semibold">{t('assistant.action.title')}</span>
        </div>
        <p className="mb-1">{action.summary}</p>
        <div className="d-flex gap-2">
          <Button variant="success" size="sm" onClick={() => void handleConfirm()} disabled={busy}>
            {t('assistant.action.confirm')}
          </Button>
          <Button
            variant="outline-secondary"
            size="sm"
            onClick={() => void handleCancel()}
            disabled={busy}
          >
            {t('assistant.action.cancel')}
          </Button>
        </div>
      </CardBody>
    </Card>
  )
}

/**
 * AI-CHAT-04: bulk "Confirmar todas" affordance shown when a turn proposed 2+ write-actions. It keeps
 * the same safety spine — nothing is written until the user explicitly clicks — but confirms every
 * pending card in one gesture by POSTing .../confirm per id sequentially. Each success invalidates the
 * affected feature query (by actionType, exactly like the single-confirm path) and removes the card via
 * onResolved; a 409/404 means that action was already decided elsewhere, so it is skipped, its feature
 * query refetched to reconcile, and the card removed too. Individual Confirmar/Cancelar remain available
 * on each card.
 */
export function ConfirmAllActionsButton({
  cards,
  onResolved,
}: {
  cards: StreamActionCard[]
  onResolved: (id: string) => void
}) {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()
  const queryClient = useQueryClient()
  const [busy, setBusy] = useState(false)

  function refetchFeature(actionType: string) {
    const key = actionInvalidationKey(actionType)
    if (key) void queryClient.invalidateQueries({ queryKey: key })
  }

  async function handleConfirmAll() {
    setBusy(true)
    let hadError = false

    // Sequential so a mid-batch failure doesn't fan out N concurrent writes; each confirm is
    // independent + idempotent server-side (a re-confirm returns 409, never a double-apply).
    for (const card of cards) {
      try {
        await apiFetch<ActionStatusResponse>(`/api/ai/actions/${card.id}/confirm`, {
          method: 'POST',
          accessToken: token,
        })
        refetchFeature(card.actionType)
        onResolved(card.id)
      } catch (error) {
        if (error instanceof ApiError && (error.status === 409 || error.status === 404)) {
          // Already decided/expired elsewhere — treat as done: reconcile the feature and drop the card.
          refetchFeature(card.actionType)
          onResolved(card.id)
        } else {
          hadError = true
        }
      }
    }

    setBusy(false)
    showNotification({
      message: hadError
        ? t('assistant.action.confirmAllError')
        : t('assistant.action.confirmAllSuccess'),
      variant: hadError ? 'danger' : 'success',
    })
  }

  return (
    <Button variant="success" size="sm" onClick={() => void handleConfirmAll()} disabled={busy}>
      <Icon icon="check" className="me-1" />
      {t('assistant.action.confirmAll')} ({cards.length})
    </Button>
  )
}
