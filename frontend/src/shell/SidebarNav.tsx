import { useState } from 'react'
import clsx from 'clsx'
import { Icon } from '@/components/Icon'
import { Collapse } from 'react-bootstrap'
import { Link, useLocation } from 'react-router'

import { useMenuItems, type NavGroupEntry, type NavLinkEntry } from './nav'
import { isAnyPathActive, isPathActive } from './navMatch'

type SidebarNavProps = {
  /** Called after any menu link is clicked (closes the mobile menu). */
  onNavigate?: () => void
  /** The desktop sidebar is shown as an icon rail. */
  collapsed?: boolean
  /** Asks the shell to leave the icon rail (a group was clicked while collapsed). */
  onExpand?: () => void
}

function NavIcon({ name }: { name: string }) {
  return (
    <span className="sf-nav__icon" aria-hidden="true">
      <Icon icon={`lucide:${name}`} />
    </span>
  )
}

type NavLinkItemProps = {
  entry: NavLinkEntry
  pathname: string
  onNavigate?: () => void
}

function NavLinkItem({ entry, pathname, onNavigate }: NavLinkItemProps) {
  const active = isPathActive(pathname, entry.to)
  return (
    <li>
      <Link
        to={entry.to}
        className={clsx('sf-nav__link', active && 'is-active')}
        aria-current={active ? 'page' : undefined}
        title={entry.label}
        onClick={onNavigate}
      >
        {entry.icon && <NavIcon name={entry.icon} />}
        <span className="sf-nav__label">{entry.label}</span>
      </Link>
    </li>
  )
}

type NavGroupItemProps = {
  entry: NavGroupEntry
  pathname: string
  open: boolean
  onToggle: () => void
  onNavigate?: () => void
}

function NavGroupItem({ entry, pathname, open, onToggle, onNavigate }: NavGroupItemProps) {
  const listId = `sf-nav-group-${entry.id}`
  const childActive = isAnyPathActive(
    pathname,
    entry.links.map((child) => child.to),
  )

  return (
    <li>
      <button
        type="button"
        className={clsx('sf-nav__link sf-nav__group-toggle', childActive && 'is-active-parent')}
        aria-expanded={open}
        aria-controls={listId}
        title={entry.label}
        onClick={onToggle}
      >
        <NavIcon name={entry.icon} />
        <span className="sf-nav__label">{entry.label}</span>
        <span className="sf-nav__chevron" aria-hidden="true" />
      </button>
      <Collapse in={open}>
        <ul id={listId} className="sf-nav sf-nav--sub">
          {entry.links.map((child) => (
            <NavLinkItem key={child.id} entry={child} pathname={pathname} onNavigate={onNavigate} />
          ))}
        </ul>
      </Collapse>
    </li>
  )
}

/** The sidebar menu: one titled list per section, one highlighted link at most. */
export function SidebarNav({ onNavigate, collapsed = false, onExpand }: SidebarNavProps) {
  const sections = useMenuItems()
  const { pathname } = useLocation()
  // Explicit open/closed choices per group; a group without one follows the route.
  const [userChoice, setUserChoice] = useState<Record<string, boolean>>({})

  function isGroupOpen(group: NavGroupEntry): boolean {
    return (
      userChoice[group.id] ??
      isAnyPathActive(
        pathname,
        group.links.map((child) => child.to),
      )
    )
  }

  function toggleGroup(group: NavGroupEntry) {
    if (collapsed) {
      // The rail cannot show the group's links: leave it and show them open.
      onExpand?.()
      setUserChoice((prev) => ({ ...prev, [group.id]: true }))
      return
    }
    const next = !isGroupOpen(group)
    setUserChoice((prev) => ({ ...prev, [group.id]: next }))
  }

  return (
    <>
      {sections.map((section) => (
        <div key={section.id} className="sf-nav__section">
          <div className="sf-nav__title">{section.title}</div>
          <ul className="sf-nav">
            {section.entries.map((entry) =>
              entry.kind === 'group' ? (
                <NavGroupItem
                  key={entry.id}
                  entry={entry}
                  pathname={pathname}
                  open={isGroupOpen(entry)}
                  onToggle={() => toggleGroup(entry)}
                  onNavigate={onNavigate}
                />
              ) : (
                <NavLinkItem key={entry.id} entry={entry} pathname={pathname} onNavigate={onNavigate} />
              ),
            )}
          </ul>
        </div>
      ))}
    </>
  )
}
