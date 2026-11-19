import { Suspense } from 'react'
import { Icon } from '@/components/Icon'
import { Spinner } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { Link, Outlet } from 'react-router'

import { useAuth } from '@/features/auth/useAuth'

import { Wordmark } from './Wordmark'

const CURRENT_YEAR = new Date().getFullYear()

const HIGHLIGHTS = [
  { key: 'consolidated', icon: 'lucide:landmark' },
  { key: 'budget', icon: 'lucide:wallet' },
  { key: 'selfHosted', icon: 'lucide:server' },
] as const

function CardLoading() {
  return (
    <div className="d-flex justify-content-center py-4">
      <Spinner animation="border" size="sm" variant="primary" />
    </div>
  )
}

/**
 * Layout route element for the account screens and the not-found page: a
 * gradient brand panel (from lg up) beside a centered card that holds the
 * screen. Below lg only the card shows, with the wordmark on top.
 */
export default function AuthLayout() {
  const { t } = useTranslation('shell')
  const { isAuthenticated } = useAuth()

  return (
    <div className="sf-auth">
      <section className="sf-auth__brand">
        <Wordmark />
        <h2 className="sf-auth__tagline">{t('authLayout.tagline')}</h2>
        <ul className="sf-auth__highlights">
          {HIGHLIGHTS.map(({ key, icon }) => (
            <li key={key}>
              <Icon icon={icon} aria-hidden="true" />
              <span>{t(`authLayout.highlights.${key}`)}</span>
            </li>
          ))}
        </ul>
      </section>
      <main className="sf-auth__main">
        <div className="sf-auth__card">
          <div className="sf-auth__card-brand d-lg-none">
            <Wordmark />
          </div>
          {isAuthenticated && (
            <Link to="/" className="sf-auth__back">
              <Icon icon="lucide:arrow-left" aria-hidden="true" />
              {t('authLayout.backToApp')}
            </Link>
          )}
          <Suspense fallback={<CardLoading />}>
            <Outlet />
          </Suspense>
          <p className="sf-auth__footer">{t('footer.copyright', { year: CURRENT_YEAR })}</p>
        </div>
      </main>
    </div>
  )
}
