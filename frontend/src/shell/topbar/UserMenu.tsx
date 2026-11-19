import { Icon } from '@/components/Icon'
import { Dropdown } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'

import { useAuth } from '@/features/auth/useAuth'

/** Topbar account menu: greeting, change password, two-factor setup and sign out. */
export function UserMenu() {
  const { t } = useTranslation('shell')
  const navigate = useNavigate()
  const { user, logout } = useAuth()
  const displayName = user?.name || user?.email || t('userDropdown.account')

  async function handleSignOut() {
    // logout() always clears the local session, even when the server call
    // fails, so the user leaves the shell either way.
    try {
      await logout()
    } finally {
      navigate('/login')
    }
  }

  return (
    <Dropdown align="end">
      <Dropdown.Toggle
        as="button"
        type="button"
        className="sf-topbar__btn sf-topbar__user"
        aria-label={t('userDropdown.account')}
        title={t('userDropdown.account')}
      >
        <Icon icon="lucide:circle-user-round" className="sf-topbar__user-icon" aria-hidden="true" />
        <span className="sf-topbar__user-name d-none d-lg-inline text-truncate">{displayName}</span>
        <Icon icon="lucide:chevron-down" className="sf-topbar__chevron d-none d-lg-inline" aria-hidden="true" />
      </Dropdown.Toggle>
      <Dropdown.Menu>
        <Dropdown.Header>
          <span className="d-block fw-semibold text-body">
            {t('userDropdown.welcomeBack', { name: user?.name || user?.email })}
          </span>
          {user?.email && <small className="d-block text-muted text-truncate">{user.email}</small>}
        </Dropdown.Header>
        <Dropdown.Item as={Link} to="/settings/change-password" className="sf-user-menu__item">
          <Icon icon="lucide:key-round" aria-hidden="true" />
          <span>{t('userDropdown.changePassword')}</span>
        </Dropdown.Item>
        <Dropdown.Item as={Link} to="/settings/2fa/enable" className="sf-user-menu__item">
          <Icon icon="lucide:shield-check" aria-hidden="true" />
          <span>{t('userDropdown.enableTwoFactor')}</span>
        </Dropdown.Item>
        <Dropdown.Divider />
        <Dropdown.Item
          as="button"
          type="button"
          className="sf-user-menu__item text-danger fw-semibold"
          onClick={() => void handleSignOut()}
        >
          <Icon icon="lucide:log-out" aria-hidden="true" />
          <span>{t('userDropdown.logOut')}</span>
        </Dropdown.Item>
      </Dropdown.Menu>
    </Dropdown>
  )
}
