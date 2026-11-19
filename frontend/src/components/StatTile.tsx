import { Card, CardBody } from 'react-bootstrap'
import clsx from 'clsx'

import { MoneyText } from '@/components/MoneyText'

/**
 * One of the dashboard/investments/net-worth stat tiles. Every tile is
 * otherwise visually equal weight -- the `accent` left-border is reserved
 * for the primary/headline stat only (e.g. Net Worth), per v1's
 * frontend/src/features/dashboard/StatTile.tsx contract.
 */
export interface StatTileProps {
  label: string
  amount: number
  variant?: 'income' | 'expense' | 'neutral'
  accent?: boolean
}

export function StatTile({ label, amount, variant = 'neutral', accent = false }: StatTileProps) {
  return (
    <Card className={clsx(accent && 'border-start border-primary border-4')}>
      <CardBody>
        <p className="text-muted small fw-semibold mb-1">{label}</p>
        <MoneyText amount={amount} variant={variant} showSign={false} className="fs-3 fw-semibold" />
      </CardBody>
    </Card>
  )
}
