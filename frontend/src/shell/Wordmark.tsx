import clsx from 'clsx'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'

type WordmarkProps = {
  /** `full` spells the app name; `short` shows the initials for the collapsed rail. */
  variant?: 'full' | 'short'
  className?: string
}

/** Text logo for SimpleFinance; links home. */
export function Wordmark({ variant = 'full', className }: WordmarkProps) {
  const { t } = useTranslation()
  const name = t('app.name')

  return (
    <Link to="/" className={clsx('sf-wordmark', className)} aria-label={variant === 'short' ? name : undefined}>
      {variant === 'short' ? 'SF' : name}
    </Link>
  )
}
