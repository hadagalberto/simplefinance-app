import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button, Form } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

/**
 * AI-CHAT-06 composer: a textarea + send button. Disabled while a turn streams
 * so the user can't overlap requests. Enter sends; Shift+Enter inserts a newline.
 */
export function ChatComposer({
  disabled,
  onSend,
}: {
  disabled: boolean
  onSend: (message: string) => void
}) {
  const { t } = useTranslation('ai')
  const [value, setValue] = useState('')

  function submit() {
    const trimmed = value.trim()
    if (!trimmed || disabled) return
    onSend(trimmed)
    setValue('')
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    submit()
  }

  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      submit()
    }
  }

  return (
    <Form onSubmit={handleSubmit} className="d-flex align-items-end gap-2">
      <Form.Control
        as="textarea"
        rows={1}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={t('assistant.placeholder')}
        disabled={disabled}
        style={{ resize: 'none' }}
      />
      <Button type="submit" variant="primary" disabled={disabled || value.trim().length === 0}>
        <Icon icon="send" className="me-1" />
        {t('assistant.send')}
      </Button>
    </Form>
  )
}
