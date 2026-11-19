import { FormControl, FormLabel } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { scorePassword, strengthLevel } from '@/features/auth/authInputLogic'

type PasswordStrengthFieldProps = {
  password: string
  setPassword: (value: string) => void
  name?: string
  id?: string
  placeholder?: string
  label?: string
  labelClassName?: string
  inputClassName?: string
  showIcon?: boolean
  autoComplete?: string
}

const BAR_COUNT = 4

/**
 * Password input for new passwords. Below the field a four-step meter shows
 * how strong the typed value is, followed by the translated level and a hint.
 * The value stays in the caller's state; only the 0-4 score is exposed.
 */
export function PasswordStrengthField({
  password,
  setPassword,
  name,
  id,
  placeholder,
  label,
  labelClassName,
  inputClassName,
  showIcon,
  autoComplete = 'new-password',
}: PasswordStrengthFieldProps) {
  const { t } = useTranslation('auth')
  const score = scorePassword(password)
  const level = strengthLevel(score)
  const levelText = t(`passwordStrength.levels.${level}`)

  return (
    <>
      {label && (
        <FormLabel htmlFor={id} className={labelClassName}>
          {label}
        </FormLabel>
      )}
      <div className={showIcon ? 'app-search' : undefined}>
        <FormControl
          type="password"
          id={id}
          name={name}
          placeholder={placeholder}
          className={inputClassName}
          autoComplete={autoComplete}
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          required
        />
        {showIcon && <Icon icon="lock-keyhole" className="app-search-icon text-muted" />}
      </div>
      <div
        className={`sf-strength sf-strength--${level}`}
        role="progressbar"
        aria-label={t('passwordStrength.label')}
        aria-valuemin={0}
        aria-valuemax={BAR_COUNT}
        aria-valuenow={score}
        aria-valuetext={levelText}
      >
        {Array.from({ length: BAR_COUNT }, (_, index) => (
          <span key={index} className={index < score ? 'sf-strength__bar is-on' : 'sf-strength__bar'} />
        ))}
      </div>
      <p className="text-muted fs-xs mb-0">
        {password.length > 0 && <strong className="me-1">{levelText}.</strong>}
        {t('passwordStrength.hint')}
      </p>
    </>
  )
}

export default PasswordStrengthField
