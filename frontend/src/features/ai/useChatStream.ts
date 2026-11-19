import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * AI-CHAT-06: the live token stream for POST /api/ai/chat.
 *
 * Why not `EventSource`: it cannot attach an `Authorization: Bearer` header
 * (T-17-23 — the JWT must ride the header, never the URL). Why not `apiFetch`:
 * it reads the whole body via `response.text()` and cannot stream. So this hook
 * uses `fetch` + `response.body.getReader()` with a TextDecoder SSE line-parser,
 * dispatching each blank-line-separated frame on its `event:` line
 * (token | tool | action | done | error) exactly as 17-05/17-06 emit them
 * (camelCase `data:` payload). A proposal turn may emit SEVERAL `action` frames
 * (one confirmation card per staged write) and then ENDS — no `done` follows — so
 * the first `action` frame also settles the status; every card is accumulated.
 */

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL as string

export interface StreamActionCard {
  id: string
  actionType: string
  summary: string
}

export interface ChatStreamUsage {
  promptTokens: number
  completionTokens: number
}

export type ChatStreamStatus = 'idle' | 'streaming' | 'done' | 'error'

/** The `data:` payload shape shared by every ChatEvent frame (all fields optional per event). */
interface ChatFrameData {
  type?: string
  text?: string
  toolName?: string
  action?: StreamActionCard
  messageId?: string
  usage?: ChatStreamUsage
}

/**
 * The settled snapshot `send` resolves with. Returned (not just set as state) so
 * the caller can commit the turn synchronously after `await` — reading the hook's
 * state values there would be stale (captured in the calling render's closure).
 */
export interface ChatStreamResult {
  text: string
  tools: string[]
  /** Every proposed write-action from this turn's `action` frames (0, 1, or many). */
  actions: StreamActionCard[]
  status: 'done' | 'error'
  messageId: string | null
  /** 'network' for a transport failure, else the server `error` frame text (e.g. budget cap). */
  errorText: string | null
}

export interface UseChatStreamResult {
  /** The assistant prose accumulated from `token` frames (and any `error` text). */
  text: string
  /** Tool names invoked this turn, in order, from `tool` frames (transparency chips). */
  tools: string[]
  /** Proposed write-actions from this turn's `action` frames — each renders an ActionConfirmCard. */
  actions: StreamActionCard[]
  status: ChatStreamStatus
  /** Human-facing error text from an `error` frame (e.g. the monthly AI budget cap). */
  errorText: string | null
  /** The persisted assistant messageId from the terminal `done` frame. */
  messageId: string | null
  /** POST a turn and stream the reply. Resolves with the settled snapshot (done/error). */
  send: (conversationId: string | null, message: string) => Promise<ChatStreamResult>
  /** Clear all stream state back to idle (call before loading another conversation). */
  reset: () => void
  /** Abort an in-flight stream (also fired automatically on unmount). */
  cancel: () => void
}

export function useChatStream(accessToken: string): UseChatStreamResult {
  const [text, setText] = useState('')
  const [tools, setTools] = useState<string[]>([])
  const [actions, setActions] = useState<StreamActionCard[]>([])
  const [status, setStatus] = useState<ChatStreamStatus>('idle')
  const [errorText, setErrorText] = useState<string | null>(null)
  const [messageId, setMessageId] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  const reset = useCallback(() => {
    setText('')
    setTools([])
    setActions([])
    setStatus('idle')
    setErrorText(null)
    setMessageId(null)
  }, [])

  const cancel = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
  }, [])

  // Abort any in-flight stream when the consumer unmounts (T-17: don't leak the
  // server LLM stream / burn budget after the user navigates away).
  useEffect(() => () => abortRef.current?.abort(), [])

  const send = useCallback(
    async (conversationId: string | null, message: string): Promise<ChatStreamResult> => {
      abortRef.current?.abort()
      const controller = new AbortController()
      abortRef.current = controller

      setText('')
      setTools([])
      setActions([])
      setErrorText(null)
      setMessageId(null)
      setStatus('streaming')

      // Local accumulators mirror the state setters so `send` can resolve with a
      // fresh snapshot (the state values are stale inside the caller's closure).
      let localText = ''
      const localTools: string[] = []
      const localActions: StreamActionCard[] = []
      let localMessageId: string | null = null
      let localStatus: ChatStreamResult['status'] = 'done'
      let localErrorText: string | null = null

      let response: Response
      try {
        response = await fetch(`${API_BASE_URL}/api/ai/chat`, {
          method: 'POST',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${accessToken}`,
          },
          body: JSON.stringify({ conversationId, message }),
          signal: controller.signal,
        })
      } catch {
        if (!controller.signal.aborted) {
          setErrorText('network')
          setStatus('error')
        }
        return { text: '', tools: [], actions: [], status: 'error', messageId: null, errorText: 'network' }
      }

      if (!response.ok || !response.body) {
        setErrorText('network')
        setStatus('error')
        return { text: '', tools: [], actions: [], status: 'error', messageId: null, errorText: 'network' }
      }

      const handleFrame = (frame: string) => {
        const lines = frame.split('\n')
        const eventLine = lines.find((l) => l.startsWith('event:'))
        const dataLine = lines.find((l) => l.startsWith('data:'))
        if (!dataLine) return

        const event = eventLine?.slice('event:'.length).trim()
        let data: ChatFrameData
        try {
          data = JSON.parse(dataLine.slice('data:'.length).trim()) as ChatFrameData
        } catch {
          return
        }

        if (event === 'token') {
          const delta = data.text
          if (delta) {
            localText += delta
            setText((prev) => prev + delta)
          }
        } else if (event === 'tool') {
          const name = data.toolName
          if (name) {
            localTools.push(name)
            setTools((prev) => [...prev, name])
          }
        } else if (event === 'action') {
          const card = data.action
          if (card) {
            localActions.push(card)
            localStatus = 'done'
            setActions((prev) => [...prev, card])
            setStatus('done')
          }
        } else if (event === 'done') {
          if (data.messageId) {
            localMessageId = data.messageId
            setMessageId(data.messageId)
          }
          localStatus = 'done'
          setStatus('done')
        } else if (event === 'error') {
          const msg = data.text ?? 'error'
          localErrorText = msg
          localText = localText ? `${localText}\n\n${msg}` : msg
          localStatus = 'error'
          setErrorText(msg)
          setText((prev) => (prev ? `${prev}\n\n${msg}` : msg))
          setStatus('error')
        }
      }

      const reader = response.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      try {
        for (;;) {
          const { value, done } = await reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })
          const frames = buffer.split('\n\n')
          buffer = frames.pop() ?? ''
          for (const frame of frames) {
            if (frame.trim()) handleFrame(frame)
          }
        }
        if (buffer.trim()) handleFrame(buffer)
      } catch {
        if (!controller.signal.aborted) {
          setStatus('error')
          setErrorText('network')
        }
        return {
          text: localText,
          tools: localTools,
          actions: localActions,
          status: 'error',
          messageId: localMessageId,
          errorText: 'network',
        }
      } finally {
        if (abortRef.current === controller) abortRef.current = null
      }

      // If the stream ended without a terminal frame, settle to done so the
      // composer re-enables rather than hanging in 'streaming'.
      setStatus((prev) => (prev === 'streaming' ? 'done' : prev))

      return {
        text: localText,
        tools: localTools,
        actions: localActions,
        status: localStatus,
        messageId: localMessageId,
        errorText: localErrorText,
      }
    },
    [accessToken]
  )

  return { text, tools, actions, status, errorText, messageId, send, reset, cancel }
}
