import { useEffect, useRef } from 'react'
import { Alert, Placeholder, Spinner } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

import { ActionConfirmCard, ConfirmAllActionsButton } from './ActionConfirmCard'
import { ToolChip } from './ToolChip'
import type { ChatMessageDto } from './api'
import type { ChatStreamStatus, StreamActionCard } from './useChatStream'

interface ChatThreadProps {
  /** Persisted turns, the single source of truth (oldest-first). */
  messages: ChatMessageDto[]
  /** Proposed write-actions from this session awaiting the user's decision. */
  actionCards: StreamActionCard[]
  /** Remove a decided card from the pending list (used by the bulk "Confirmar todas" flow). */
  onResolveAction: (id: string) => void
  isLoading: boolean
  isError: boolean
  /** The just-sent user turn, shown optimistically while its reply streams. */
  pendingUserMessage: string | null
  streamText: string
  streamTools: string[]
  /** Proposed write-actions from the in-flight turn (0, 1, or many). */
  streamActions: StreamActionCard[]
  status: ChatStreamStatus
}

function UserBubble({ text }: { text: string }) {
  const { t } = useTranslation('ai')
  return (
    <div className="d-flex flex-column align-items-end mb-3">
      <span className="text-muted fs-xxs mb-1">{t('assistant.you')}</span>
      <div
        className="bg-primary text-white rounded px-3 py-2"
        style={{ maxWidth: '80%', whiteSpace: 'pre-wrap' }}
      >
        {text}
      </div>
    </div>
  )
}

function AssistantBubble({ text }: { text: string }) {
  const { t } = useTranslation('ai')
  return (
    <div className="d-flex flex-column align-items-start mb-3">
      <span className="text-muted fs-xxs mb-1">
        <Icon icon="bot" className="me-1" />
        {t('assistant.assistantLabel')}
      </span>
      <div
        className="bg-light bg-opacity-50 rounded px-3 py-2"
        style={{ maxWidth: '80%', whiteSpace: 'pre-wrap' }}
      >
        {text}
      </div>
    </div>
  )
}

/**
 * AI-CHAT-06 thread: persisted turns (the source of truth, refetched after each
 * turn) + any unresolved action cards + the live-streaming turn. Tool invocations
 * render as transparency chips; a proposed write renders an ActionConfirmCard.
 * Auto-scrolls to the newest content.
 */
export function ChatThread({
  messages,
  actionCards,
  onResolveAction,
  isLoading,
  isError,
  pendingUserMessage,
  streamText,
  streamTools,
  streamActions,
  status,
}: ChatThreadProps) {
  const { t } = useTranslation('ai')
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, actionCards, pendingUserMessage, streamText, streamTools, streamActions, status])

  if (isLoading) {
    return (
      <div className="d-flex flex-column gap-3 p-3">
        {Array.from({ length: 4 }).map((_, i) => (
          <Placeholder key={i} as="div" animation="glow">
            <Placeholder xs={i % 2 === 0 ? 7 : 5} style={{ height: 40 }} />
          </Placeholder>
        ))}
      </div>
    )
  }

  if (isError) {
    return (
      <Alert variant="danger" className="m-3">
        {t('assistant.threadLoadError')}
      </Alert>
    )
  }

  const isEmpty =
    messages.length === 0 &&
    actionCards.length === 0 &&
    pendingUserMessage === null &&
    streamText.length === 0 &&
    streamTools.length === 0 &&
    streamActions.length === 0

  if (isEmpty) {
    return (
      <div className="text-center text-muted py-5">
        <Icon icon="message-circle" className="fs-1 mb-3" />
        <p className="mb-0">{t('assistant.threadEmpty')}</p>
      </div>
    )
  }

  const streaming = status === 'streaming'

  return (
    <div className="p-3">
      {messages.map((message) => {
        if (message.role === 'Tool') {
          return (
            <div key={message.id} className="mb-2">
              <ToolChip tool={message.toolName ?? '?'} />
            </div>
          )
        }
        if (message.role === 'User') {
          return <UserBubble key={message.id} text={message.content ?? ''} />
        }
        if ((message.content ?? '').length === 0) return null
        return <AssistantBubble key={message.id} text={message.content ?? ''} />
      })}

      {actionCards.length >= 2 && (
        <div className="mb-2" style={{ maxWidth: '80%' }}>
          <ConfirmAllActionsButton cards={actionCards} onResolved={onResolveAction} />
        </div>
      )}

      {actionCards.map((card) => (
        <div key={card.id} className="mb-3" style={{ maxWidth: '80%' }}>
          <ActionConfirmCard action={card} />
        </div>
      ))}

      {pendingUserMessage !== null && (
        <>
          <UserBubble text={pendingUserMessage} />
          {streamTools.length > 0 && (
            <div className="d-flex flex-wrap gap-1 mb-2">
              {streamTools.map((tool, i) => (
                <ToolChip key={`${tool}-${i}`} tool={tool} />
              ))}
            </div>
          )}
          {streaming && streamText.length === 0 && streamTools.length === 0 && (
            <div className="d-flex align-items-center gap-2 text-muted mb-3">
              <Spinner animation="border" size="sm" />
              {t('assistant.thinking')}
            </div>
          )}
          {streamText.length > 0 && <AssistantBubble text={streamText} />}
          {streamActions.map((card) => (
            <div key={card.id} className="mb-3" style={{ maxWidth: '80%' }}>
              <ActionConfirmCard action={card} />
            </div>
          ))}
        </>
      )}

      <div ref={bottomRef} />
    </div>
  )
}
