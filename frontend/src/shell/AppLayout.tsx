import { Suspense } from 'react'
import clsx from 'clsx'
import { Spinner } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { Outlet } from 'react-router'

import { Sidebar } from './Sidebar'
import { Topbar } from './Topbar'
import { useSidebarState } from './useSidebarState'

const CURRENT_YEAR = new Date().getFullYear()

/** Matches Bootstrap's lg breakpoint: the sidebar is inline from here up. */
const DESKTOP_QUERY = '(min-width: 992px)'

function isDesktop(): boolean {
  return window.matchMedia(DESKTOP_QUERY).matches
}

function PageLoading() {
  return (
    <div className="d-flex justify-content-center py-5">
      <Spinner animation="border" variant="primary" />
    </div>
  )
}

/** Layout route element for every signed-in screen: sidebar, topbar, page, footer. */
export default function AppLayout() {
  const { t } = useTranslation('shell')
  const { collapsed, toggleCollapsed, expand, mobileOpen, openMobile, closeMobile } = useSidebarState()

  function handleToggleMenu() {
    if (isDesktop()) toggleCollapsed()
    else openMobile()
  }

  // The rail only exists from lg up; below that the menu is the offcanvas,
  // which must never act on (or overwrite) the stored desktop choice.
  const desktop = isDesktop()
  const menuExpanded = desktop ? !collapsed : mobileOpen

  return (
    <div className={clsx('sf-shell', collapsed && 'sf-shell--collapsed')}>
      <Sidebar collapsed={collapsed && desktop} onExpand={expand} mobileOpen={mobileOpen} onCloseMobile={closeMobile} />
      <div className="sf-shell__main">
        <Topbar onToggleMenu={handleToggleMenu} menuExpanded={menuExpanded} />
        <main className="sf-content container-fluid">
          <Suspense fallback={<PageLoading />}>
            <Outlet />
          </Suspense>
        </main>
        <footer className="sf-footer">{t('footer.copyright', { year: CURRENT_YEAR })}</footer>
      </div>
    </div>
  )
}
