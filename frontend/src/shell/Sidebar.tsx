import { Offcanvas } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { SidebarNav } from './SidebarNav'
import { Wordmark } from './Wordmark'

type SidebarProps = {
  collapsed: boolean
  onExpand: () => void
  mobileOpen: boolean
  onCloseMobile: () => void
}

/**
 * Left navigation column of the app shell. From 992px up the panel renders
 * inline inside the aside (which carries the surface and border); below that
 * it opens as an offcanvas over a backdrop.
 */
export function Sidebar({ collapsed, onExpand, mobileOpen, onCloseMobile }: SidebarProps) {
  const { t } = useTranslation('shell')

  return (
    <aside className="sf-sidebar">
      <Offcanvas
        responsive="lg"
        placement="start"
        show={mobileOpen}
        onHide={onCloseMobile}
        className="sf-sidebar__panel"
        aria-label={t('sidebar.navLabel')}
      >
        <Offcanvas.Header closeButton closeLabel={t('sidebar.close')} />
        <Offcanvas.Body>
          <div className="sf-sidebar__brand">
            <Wordmark className="sf-sidebar__brand-full" />
            <Wordmark variant="short" className="sf-sidebar__brand-short" />
          </div>
          <nav className="sf-sidebar__nav" aria-label={t('sidebar.navLabel')}>
            <SidebarNav onNavigate={onCloseMobile} collapsed={collapsed} onExpand={onExpand} />
          </nav>
        </Offcanvas.Body>
      </Offcanvas>
    </aside>
  )
}
