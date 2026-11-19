import { Icon } from '@/components/Icon'
import { Dropdown } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import type { ThemePreference } from '../theme/themeLogic'
import { useTheme } from '../theme/useTheme'

const OPTIONS: { value: ThemePreference; icon: string; labelKey: 'theme.light' | 'theme.dark' | 'theme.system' }[] = [
  { value: 'light', icon: 'lucide:sun', labelKey: 'theme.light' },
  { value: 'dark', icon: 'lucide:moon', labelKey: 'theme.dark' },
  { value: 'system', icon: 'lucide:sun-moon', labelKey: 'theme.system' },
]

/** Topbar dropdown to pick light, dark or system (follow the OS) theme. */
export function ThemeMenu() {
  const { t } = useTranslation('shell')
  const { theme, setTheme } = useTheme()
  const current = OPTIONS.find((option) => option.value === theme) ?? OPTIONS[1]

  return (
    <Dropdown align="end">
      <Dropdown.Toggle as="button" type="button" className="sf-topbar__btn" aria-label={t('theme.label')} title={t('theme.label')}>
        <Icon icon={current.icon} aria-hidden="true" />
      </Dropdown.Toggle>
      <Dropdown.Menu>
        {OPTIONS.map((option) => (
          <Dropdown.Item
            key={option.value}
            as="button"
            type="button"
            className="sf-theme-menu__item"
            active={option.value === theme}
            aria-current={option.value === theme ? 'true' : undefined}
            onClick={() => setTheme(option.value)}
          >
            <Icon icon={option.icon} aria-hidden="true" />
            <span>{t(option.labelKey)}</span>
          </Dropdown.Item>
        ))}
      </Dropdown.Menu>
    </Dropdown>
  )
}
