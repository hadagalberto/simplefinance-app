import { useState } from 'react'
import { Button, Card, CardBody, Col, ListGroup, Placeholder, Row } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'

import { ChatComposer } from './ChatComposer'
import { ChatThread } from './ChatThread'
import {
  useConversationQuery,
  useConversationsQuery,
  useDeleteConversationMutation,
  useRenameConversationMutation,
  type ConversationSummary,
} from './api'
import { useChatStream, type StreamActionCard } from './useChatStream'

/**
 * AI-CHAT-06: the /assistente page. A conversation sidebar (new / rename / delete
 * via TanStack Query) beside a live-streaming thread driven by useChatStream
 * (fetch + getReader, JWT bearer). Persisted messages are the single source of
 * truth — refetched after each completed turn — so the only session-local state
 * is the in-flight streaming turn and any proposed write-actions awaiting the
 * user's decision (their ActionConfirmCards outlive the stream since a proposal
 * is not a persisted message). A brand-new conversation adopts its server-created
 * id after the first turn so follow-ups continue the same thread.
 */
export function AssistantPage() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const [conversationId, setConversationId] = useState<string | null>(null)
  const [pendingUserMessage, setPendingUserMessage] = useState<string | null>(null)
  const [actionCards, setActionCards] = useState<StreamActionCard[]>([])

  const conversationsQuery = useConversationsQuery(token)
  const conversationQuery = useConversationQuery(token, conversationId)
  const renameMutation = useRenameConversationMutation()
  const deleteMutation = useDeleteConversationMutation()
  const chat = useChatStream(token)

  const conversations = conversationsQuery.data ?? []
  const messages = conversationQuery.data?.messages ?? []
  const isStreaming = chat.status === 'streaming' || pendingUserMessage !== null

  function handleResolveAction(id: string) {
    setActionCards((prev) => prev.filter((card) => card.id !== id))
  }

  function handleNewConversation() {
    if (isStreaming) return
    setConversationId(null)
    setActionCards([])
    setPendingUserMessage(null)
    chat.reset()
  }

  function handleSelectConversation(id: string) {
    if (isStreaming || id === conversationId) return
    setConversationId(id)
    setActionCards([])
    setPendingUserMessage(null)
    chat.reset()
  }

  async function handleSend(message: string) {
    const currentId = conversationId
    setPendingUserMessage(message)

    const result = await chat.send(currentId, message)

    if (result.status === 'error') {
      showNotification({
        message:
          result.errorText === 'network'
            ? t('assistant.networkError')
            : (result.errorText ?? t('assistant.streamError')),
        variant: 'danger',
      })
      setPendingUserMessage(null)
      chat.reset()
      return
    }

    // Refresh the list (titles / recency reorder) and resolve which conversation
    // now owns this turn: adopt the server-created id for a brand-new chat, else
    // refetch the open thread so its newly-persisted messages replace the live turn.
    const refreshedList = await conversationsQuery.refetch()
    if (currentId === null) {
      const newest = refreshedList.data?.[0]
      if (newest) setConversationId(newest.id)
    } else {
      await conversationQuery.refetch()
    }

    if (result.actions.length > 0) {
      setActionCards((prev) => [...prev, ...result.actions])
    }
    setPendingUserMessage(null)
    chat.reset()
  }

  async function handleRename(conversation: ConversationSummary) {
    const nextTitle = window.prompt(t('assistant.renamePrompt'), conversation.title)
    if (nextTitle === null) return
    const trimmed = nextTitle.trim()
    if (trimmed.length === 0) return
    try {
      await renameMutation.mutateAsync({ accessToken: token, id: conversation.id, title: trimmed })
      showNotification({ message: t('assistant.renameSuccess'), variant: 'success' })
    } catch {
      showNotification({ message: t('assistant.renameError'), variant: 'danger' })
    }
  }

  async function handleDelete(conversation: ConversationSummary) {
    if (!window.confirm(t('assistant.deleteConfirm'))) return
    try {
      await deleteMutation.mutateAsync({ accessToken: token, id: conversation.id })
      showNotification({ message: t('assistant.deleteSuccess'), variant: 'success' })
      if (conversation.id === conversationId) {
        setConversationId(null)
        setActionCards([])
        setPendingUserMessage(null)
        chat.reset()
      }
    } catch {
      showNotification({ message: t('assistant.deleteError'), variant: 'danger' })
    }
  }

  const threadLoading =
    conversationId !== null && conversationQuery.isPending && pendingUserMessage === null
  const threadError =
    conversationId !== null && conversationQuery.isError && pendingUserMessage === null

  return (
    <Row className="g-3">
      <Col lg={3} md={4}>
        <Card className="h-100">
          <CardBody className="d-flex flex-column">
            <div className="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
              <h5 className="mb-0">{t('assistant.conversationsTitle')}</h5>
              <Button
                variant="primary"
                size="sm"
                onClick={handleNewConversation}
                disabled={isStreaming}
              >
                <Icon icon="plus" className="me-1" />
                {t('assistant.newConversation')}
              </Button>
            </div>

            {conversationsQuery.isPending && (
              <div className="d-flex flex-column gap-2">
                {Array.from({ length: 5 }).map((_, i) => (
                  <Placeholder key={i} as="div" animation="glow">
                    <Placeholder xs={12} style={{ height: 28 }} />
                  </Placeholder>
                ))}
              </div>
            )}

            {conversationsQuery.isError && (
              <p className="text-danger fs-sm mb-0">{t('assistant.conversationsLoadError')}</p>
            )}

            {!conversationsQuery.isPending &&
              !conversationsQuery.isError &&
              conversations.length === 0 && (
                <p className="text-muted fs-sm mb-0">{t('assistant.conversationsEmpty')}</p>
              )}

            {conversations.length > 0 && (
              <ListGroup variant="flush" className="overflow-auto">
                {conversations.map((conversation) => (
                  <ListGroup.Item
                    key={conversation.id}
                    action
                    active={conversation.id === conversationId}
                    onClick={() => handleSelectConversation(conversation.id)}
                    className="d-flex align-items-center justify-content-between gap-2 px-2"
                  >
                    <span className="text-truncate">{conversation.title}</span>
                    <span className="d-flex gap-1 flex-shrink-0">
                      <Button
                        variant="link"
                        size="sm"
                        className="p-0 text-reset"
                        title={t('assistant.rename')}
                        onClick={(e) => {
                          e.stopPropagation()
                          void handleRename(conversation)
                        }}
                      >
                        <Icon icon="pencil" className="fs-sm" />
                      </Button>
                      <Button
                        variant="link"
                        size="sm"
                        className="p-0 text-danger"
                        title={t('assistant.delete')}
                        onClick={(e) => {
                          e.stopPropagation()
                          void handleDelete(conversation)
                        }}
                      >
                        <Icon icon="trash-2" className="fs-sm" />
                      </Button>
                    </span>
                  </ListGroup.Item>
                ))}
              </ListGroup>
            )}
          </CardBody>
        </Card>
      </Col>

      <Col lg={9} md={8}>
        <Card className="h-100">
          <CardBody className="d-flex flex-column p-0" style={{ minHeight: '60vh' }}>
            <div className="border-bottom px-3 py-2">
              <h5 className="mb-0">{t('assistant.title')}</h5>
              <p className="text-muted fs-xs mb-0">{t('assistant.subtitle')}</p>
            </div>

            <div className="flex-grow-1 overflow-auto" style={{ maxHeight: '55vh' }}>
              <ChatThread
                messages={messages}
                actionCards={actionCards}
                onResolveAction={handleResolveAction}
                isLoading={threadLoading}
                isError={threadError}
                pendingUserMessage={pendingUserMessage}
                streamText={chat.text}
                streamTools={chat.tools}
                streamActions={chat.actions}
                status={chat.status}
              />
            </div>

            <div className="border-top p-3">
              <ChatComposer disabled={isStreaming} onSend={(m) => void handleSend(m)} />
            </div>
          </CardBody>
        </Card>
      </Col>
    </Row>
  )
}
