import { Dropdown } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { LANGUAGE_STORAGE_KEY, type SupportedLanguage } from '@/i18n'

type LanguageOption = {
  code: SupportedLanguage
  /** Endonym: each language is named in itself, so it is never translated. */
  label: string
  short: string
  flag: string
}

const OPTIONS: LanguageOption[] = [
  { code: 'pt-BR', label: 'Português', short: 'PT', flag: '🇧🇷' },
  { code: 'en', label: 'English', short: 'EN', flag: '🇺🇸' },
]

/** Topbar dropdown that switches the UI language between pt-BR and English. */
export function LanguageMenu() {
  const { t, i18n } = useTranslation('shell')
  const active = i18n.resolvedLanguage ?? i18n.language
  const current = OPTIONS.find((option) => option.code === active) ?? OPTIONS[0]

  function handleSelect(code: SupportedLanguage) {
    void i18n.changeLanguage(code)
    try {
      localStorage.setItem(LANGUAGE_STORAGE_KEY, code)
    } catch {
      // Storage can be blocked (private mode, quota); the switch still applies for this session.
    }
  }

  return (
    <Dropdown align="end">
      <Dropdown.Toggle
        as="button"
        type="button"
        className="sf-topbar__btn sf-topbar__lang"
        aria-label={t('language.label')}
        title={t('language.label')}
      >
        <span className="sf-topbar__flag" aria-hidden="true">
          {current.flag}
        </span>
        <span>{current.short}</span>
      </Dropdown.Toggle>
      <Dropdown.Menu>
        {OPTIONS.map((option) => (
          <Dropdown.Item
            key={option.code}
            as="button"
            type="button"
            className="sf-lang-menu__item"
            lang={option.code}
            active={option.code === current.code}
            aria-current={option.code === current.code ? 'true' : undefined}
            onClick={() => handleSelect(option.code)}
          >
            <span className="sf-topbar__flag" aria-hidden="true">
              {option.flag}
            </span>
            <span>{option.label}</span>
          </Dropdown.Item>
        ))}
      </Dropdown.Menu>
    </Dropdown>
  )
}
