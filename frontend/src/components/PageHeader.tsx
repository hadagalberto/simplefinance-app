import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'

import { usePageTitle } from '@/shell/usePageTitle'

type PageHeaderProps = {
  /** Translated screen name: the heading, the last breadcrumb step and the tab title. */
  title: string
  /** Translated parent group (for example the settings group). Plain text: the group has no page of its own. */
  section?: string
  /** Buttons or other controls shown at the end of the header row. */
  actions?: ReactNode
}

/**
 * Opening row of every signed-in screen: heading, breadcrumb trail back to the
 * dashboard and an optional actions area. It also sets the browser tab title.
 */
export function PageHeader({ title, section, actions }: PageHeaderProps) {
  const { t } = useTranslation(['shell', 'common'])
  usePageTitle(title)

  return (
    <div className="sf-page-header">
      <div className="sf-page-header__text">
        <h1 className="sf-page-header__title">{title}</h1>
        <nav aria-label={t('shell:pageHeader.breadcrumb')}>
          <ol className="breadcrumb sf-page-header__crumbs mb-0">
            <li className="breadcrumb-item">
              <Link to="/">{t('common:app.name')}</Link>
            </li>
            {section && <li className="breadcrumb-item">{section}</li>}
            <li className="breadcrumb-item active" aria-current="page">
              {title}
            </li>
          </ol>
        </nav>
      </div>
      {actions && <div className="sf-page-header__actions">{actions}</div>}
    </div>
  )
}

export default PageHeader
