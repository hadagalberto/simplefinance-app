import { Icon } from '@/components/Icon'
import { useTranslation } from 'react-i18next'

import { NotificationBell } from '@/features/notifications/NotificationBell'

import { FullscreenButton } from './topbar/FullscreenButton'
import { LanguageMenu } from './topbar/LanguageMenu'
import { ThemeMenu } from './topbar/ThemeMenu'
import { UserMenu } from './topbar/UserMenu'
import { Wordmark } from './Wordmark'

type TopbarProps = {
  onToggleMenu: () => void
  /** Whether the menu the button controls is currently shown expanded/open. */
  menuExpanded: boolean
}

/** Top bar of the app shell: menu button and brand on the left, account tools on the right. */
export function Topbar({ onToggleMenu, menuExpanded }: TopbarProps) {
  const { t } = useTranslation('shell')

  return (
    <header className="sf-topbar">
      <div className="sf-topbar__start">
        <button
          type="button"
          className="sf-topbar__toggle"
          aria-label={t('topbar.toggleMenu')}
          aria-expanded={menuExpanded}
          onClick={onToggleMenu}
        >
          <Icon icon="lucide:menu" aria-hidden="true" />
        </button>
        {/* Below 576px the short wordmark leaves room for the account tools. */}
        <Wordmark variant="short" className="d-sm-none" />
        <Wordmark className="d-none d-sm-inline d-lg-none" />
      </div>
      <div className="sf-topbar__end">
        <ThemeMenu />
        <NotificationBell />
        <FullscreenButton />
        <LanguageMenu />
        <UserMenu />
      </div>
    </header>
  )
}
