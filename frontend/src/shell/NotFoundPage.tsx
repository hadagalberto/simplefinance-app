import { Icon } from '@/components/Icon'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'

import { DocumentTitle } from './DocumentTitle'

/** Catch-all page for unknown routes, shown inside AuthLayout signed in or out. */
export default function NotFoundPage() {
  const { t } = useTranslation('shell')

  return (
    <>
      <DocumentTitle title={t('notFound.title')} />
      <p className="sf-notfound__code">404</p>
      <h1 className="sf-auth__title">{t('notFound.title')}</h1>
      <p className="sf-auth__subtitle mb-4">{t('notFound.description')}</p>
      <Link to="/" className="btn btn-primary w-100">
        <Icon icon="lucide:layout-dashboard" className="me-2" aria-hidden="true" />
        {t('notFound.back')}
      </Link>
    </>
  )
}
