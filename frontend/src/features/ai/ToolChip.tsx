import { Badge } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

/**
 * AI-CHAT-06 transparency chip: shows one tool the assistant invoked this turn
 * ("consultei sum_by_category"), so the user can see the answer is grounded in a
 * real query rather than model guesswork.
 */
export function ToolChip({ tool }: { tool: string }) {
  const { t } = useTranslation('ai')
  return (
    <Badge bg="secondary" className="d-inline-flex align-items-center gap-1 fw-normal">
      <Icon icon="wrench" className="fs-xs" />
      {t('assistant.toolUsed', { tool })}
    </Badge>
  )
}
