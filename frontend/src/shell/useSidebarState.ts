import { useState } from 'react'
import { useLocation } from 'react-router'

import { parseCollapsedFlag, SIDEBAR_COLLAPSED_KEY } from './navMatch'

function readCollapsed(): boolean {
  try {
    return parseCollapsedFlag(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY))
  } catch {
    // Storage blocked or unavailable: start expanded.
    return false
  }
}

function writeCollapsed(collapsed: boolean): void {
  try {
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, collapsed ? '1' : '0')
  } catch {
    // Storage blocked or full: the choice just does not survive a reload.
  }
}

export type SidebarState = {
  /** Desktop (992px and up): the sidebar shows as an icon rail. Persisted. */
  collapsed: boolean
  toggleCollapsed: () => void
  expand: () => void
  /** Small screens: the menu is open as an offcanvas. */
  mobileOpen: boolean
  openMobile: () => void
  closeMobile: () => void
}

/**
 * Sidebar state for the app shell. The mobile menu remembers the path it was
 * opened on and counts as open only while that path is current, so any
 * navigation closes it without an effect.
 */
export function useSidebarState(): SidebarState {
  const { pathname } = useLocation()
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [openedAt, setOpenedAt] = useState<string | null>(null)

  function setAndPersist(next: boolean) {
    setCollapsed(next)
    writeCollapsed(next)
  }

  return {
    collapsed,
    toggleCollapsed: () => setAndPersist(!collapsed),
    expand: () => setAndPersist(false),
    mobileOpen: openedAt === pathname,
    openMobile: () => setOpenedAt(pathname),
    closeMobile: () => setOpenedAt(null),
  }
}
