import { OtpCodeInput } from '@/features/auth/OtpCodeInput'
import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Alert, Button, Form, FormControl, Spinner } from 'react-bootstrap'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'

import { useVerifyTwoFactor } from '@/features/auth/api'
import { twoFactorVerifySchema, type TwoFactorVerifyFormValues } from '@/features/auth/schemas'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'
import { DocumentTitle } from '@/shell/DocumentTitle'

interface TwoFactorLocationState {
  pendingToken?: string
}

/**
 * Pre-auth screen, rendered inside AuthLayout, reached only via LoginPage's
 * navigate('/2fa/verify', { state: { pendingToken } }) — never a
 * bare/bookmarkable URL (T-10-07). Both the
 * default 6-digit OtpCodeInput mode and the "recovery code" text-input mode
 * submit through the same useVerifyTwoFactor() mutation and the same `code`
 * form field.
 */
const TwoFactorVerifyPage = () => {
  const { t } = useTranslation('auth')
  const location = useLocation()
  const navigate = useNavigate()
  const { setSession } = useAuth()
  const verifyMutation = useVerifyTwoFactor()
  const [useRecoveryCode, setUseRecoveryCode] = useState(false)
  const [digits, setDigits] = useState<string[]>(Array(6).fill(''))

  const pendingToken = (location.state as TwoFactorLocationState | null)?.pendingToken

  const form = useForm<TwoFactorVerifyFormValues>({
    resolver: zodResolver(twoFactorVerifySchema),
    defaultValues: { code: '' },
  })

  // T-10-07: redirect straight to /login if this page is opened without a pendingToken
  // (bookmarked/direct-navigated) — it must never be reachable as a bypass of login.
  if (!pendingToken) {
    return <Navigate to="/login" replace />
  }

  const errorCopy = (() => {
    if (!verifyMutation.isError) return null
    const error = verifyMutation.error
    if (error instanceof ApiError) {
      if (error.error === 'invalid_code') return t('twofa.verify.invalidCode')
      if (error.error === 'recovery_code_already_used') return t('twofa.verify.recoveryAlreadyUsed')
    }
    return t('errors.generic')
  })()

  function handleDigitsChange(next: string[]) {
    setDigits(next)
    form.setValue('code', next.join(''), { shouldValidate: form.formState.isSubmitted })
  }

  async function onSubmit(values: TwoFactorVerifyFormValues) {
    try {
      const response = await verifyMutation.mutateAsync({ pendingToken: pendingToken!, code: values.code })
      setSession({
        accessToken: response.accessToken,
        accessTokenExpiresAt: response.accessTokenExpiresAt,
        user: response.user,
      })
      navigate('/')
    } catch {
      // Surfaced via errorCopy above (invalid_code vs recovery_code_already_used vs generic).
    }
  }

  function toggleMode() {
    setUseRecoveryCode((previous) => !previous)
    setDigits(Array(6).fill(''))
    form.setValue('code', '')
    verifyMutation.reset()
  }

  return (
    <>
      <DocumentTitle title={t('twofa.verify.title')} />
      <h1 className="sf-auth__title">{t('twofa.verify.title')}</h1>
      <p className="sf-auth__subtitle">
        {useRecoveryCode ? t('twofa.verify.subtitleRecovery') : t('twofa.verify.subtitleCode')}
      </p>

      {errorCopy && (
        <Alert variant="danger" className="mt-3">
          {errorCopy}
        </Alert>
      )}

      <Form className="mt-4" onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <div className="mb-3">
          {useRecoveryCode ? (
            <>
              <Form.Label>
                {t('twofa.verify.recoveryLabel')}&nbsp;
                <span className="text-danger">*</span>
              </Form.Label>
              <FormControl
                autoComplete="one-time-code"
                placeholder={t('twofa.verify.recoveryPlaceholder')}
                isInvalid={!!form.formState.errors.code}
                {...form.register('code')}
              />
            </>
          ) : (
            <OtpCodeInput code={digits} setCode={handleDigitsChange} label={t('twofa.verify.otpLabel')} />
          )}
          {form.formState.errors.code && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.code.message ?? '')}</div>}
        </div>

        <div className="d-grid">
          <Button variant="primary" type="submit" className="fw-semibold py-2" disabled={verifyMutation.isPending}>
            {verifyMutation.isPending && <Spinner as="span" animation="border" size="sm" className="me-2" />}
            {t('twofa.verify.submit')}
          </Button>
        </div>
        <div className="text-center mt-3">
          <button type="button" onClick={toggleMode} className="btn btn-link text-decoration-underline link-offset-3 fw-semibold p-0">
            {useRecoveryCode ? t('twofa.verify.useAuthenticatorInstead') : t('twofa.verify.useRecoveryInstead')}
          </button>
        </div>
      </Form>
    </>
  )
}

export default TwoFactorVerifyPage
