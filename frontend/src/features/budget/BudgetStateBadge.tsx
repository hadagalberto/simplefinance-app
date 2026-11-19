import { Badge } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'

import type { BudgetState } from './api'

export interface BudgetStateBadgeProps {
  state: BudgetState
}

const STATE_CONFIG: Record<BudgetState, { labelKey: string; icon: string; bg: string; text: string }> = {
  Ok: { labelKey: 'state.ok', icon: 'check-circle-2', bg: 'success-subtle', text: 'success' },
  Attention: { labelKey: 'state.attention', icon: 'alert-triangle', bg: 'warning-subtle', text: 'warning' },
  Exceeded: { labelKey: 'state.exceeded', icon: 'x-circle', bg: 'danger-subtle', text: 'danger' },
}

/**
 * Single source-of-truth state badge so styling never drifts between the
 * Dashboard's AtRiskBudgetCard and this Budget table's rows (mirrors v1's
 * BudgetStateBadge.tsx). Icon always renders alongside the text label --
 * never a color-only signal (12-CONTEXT.md's UI-conventions decision).
 */
export function BudgetStateBadge({ state }: BudgetStateBadgeProps) {
  const { t } = useTranslation('budget')
  // Defensive: fall back to the "Ok" config if the API ever sends an unmapped/legacy state value
  // (e.g. a numeric enum), so a badge never crashes the whole page on an unexpected value.
  const { labelKey, icon, bg, text } = STATE_CONFIG[state] ?? STATE_CONFIG.Ok

  return (
    <Badge bg={bg} text={text} className="d-inline-flex align-items-center gap-1">
      <Icon icon={icon} />
      {t(labelKey)}
    </Badge>
  )
}
