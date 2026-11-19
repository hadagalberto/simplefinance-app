import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react'
import { CloseButton, Toast, ToastBody, ToastContainer, ToastHeader } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { ToastContext, type ToastApi, type ToastRequest, type ToastVariant } from './ToastContext'

/** Most toasts on screen at once; a new one past this pushes out the oldest. */
const MAX_VISIBLE = 4
const DEFAULT_DELAY_MS = 4000
const DEFAULT_VARIANT: ToastVariant = 'secondary'

/** Variants with a dark background, where the close button must be light. */
const DARK_BACKGROUNDS: ReadonlySet<ToastVariant> = new Set(['primary', 'secondary', 'success', 'danger', 'dark'])

type ActiveToast = { id: number; request: ToastRequest }

/**
 * Shows short status messages as a stack of toasts in the top-right corner.
 * Each toast closes by itself after its delay or at once from its close
 * button, and several can be visible together.
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation('common')
  const [toasts, setToasts] = useState<ActiveToast[]>([])
  const nextId = useRef(0)

  const showNotification = useCallback((request: ToastRequest) => {
    nextId.current += 1
    const id = nextId.current
    setToasts((current) => [...current, { id, request }].slice(-MAX_VISIBLE))
  }, [])

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id))
  }, [])

  const value = useMemo<ToastApi>(() => ({ showNotification }), [showNotification])
  const closeLabel = t('actions.close')

  return (
    <ToastContext value={value}>
      {children}
      <ToastContainer position="top-end" containerPosition="fixed" className="p-3">
        {toasts.map(({ id, request }) => {
          const variant = request.variant ?? DEFAULT_VARIANT
          return (
            <Toast
              key={id}
              autohide
              delay={request.delay ?? DEFAULT_DELAY_MS}
              onClose={() => dismiss(id)}
              className={`text-bg-${variant}`}
            >
              {request.title ? (
                <>
                  <ToastHeader closeLabel={closeLabel}>
                    <strong className="me-auto">{request.title}</strong>
                  </ToastHeader>
                  <ToastBody>{request.message}</ToastBody>
                </>
              ) : (
                <ToastBody className="d-flex align-items-start gap-2">
                  <span className="flex-grow-1">{request.message}</span>
                  <CloseButton
                    aria-label={closeLabel}
                    variant={DARK_BACKGROUNDS.has(variant) ? 'white' : undefined}
                    onClick={() => dismiss(id)}
                  />
                </ToastBody>
              )}
            </Toast>
          )
        })}
      </ToastContainer>
    </ToastContext>
  )
}
