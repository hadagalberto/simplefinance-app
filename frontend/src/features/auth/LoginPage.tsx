import Icon from '@/components/Icon'
import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Alert, Button, Form, FormControl, FormLabel, Spinner } from 'react-bootstrap'
import { useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'

import { useLogin } from '@/features/auth/api'
import { loginSchema, type LoginFormValues } from '@/features/auth/schemas'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'
import { DocumentTitle } from '@/shell/DocumentTitle'

interface LoginLocationState {
  flash?: string
}

/**
 * Pre-auth sign-in screen, rendered inside AuthLayout. On a
 * requiresTwoFactor:true response it hands the pendingToken to
 * TwoFactorVerifyPage through router state only, never the URL (T-10-06).
 */
const LoginPage = () => {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const location = useLocation()
  const { setSession } = useAuth()
  const loginMutation = useLogin()

  const [flash, setFlash] = useState(() => (location.state as LoginLocationState | null)?.flash)

  const form = useForm<LoginFormValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
  })

  const isInvalidCredentials =
    loginMutation.isError &&
    loginMutation.error instanceof ApiError &&
    loginMutation.error.status === 401 &&
    loginMutation.error.error === 'invalid_credentials'

  const isGenericError = loginMutation.isError && !isInvalidCredentials

  async function onSubmit(values: LoginFormValues) {
    try {
      const response = await loginMutation.mutateAsync(values)

      if (response.requiresTwoFactor) {
        navigate('/2fa/verify', { state: { pendingToken: response.pendingToken } })
        return
      }

      // requiresTwoFactor === false guarantees accessToken/accessTokenExpiresAt/user are set.
      setSession({
        accessToken: response.accessToken!,
        accessTokenExpiresAt: response.accessTokenExpiresAt!,
        user: response.user!,
      })
      navigate('/')
    } catch {
      // Errors are surfaced via loginMutation.isError below (invalid_credentials vs generic).
    }
  }

  return (
    <>
      <DocumentTitle title={t('login.title')} />
      <h1 className="sf-auth__title">{t('login.title')}</h1>
      <p className="sf-auth__subtitle">{t('login.subtitle')}</p>

      {flash && (
        <Alert variant="info" className="mt-3" dismissible onClose={() => setFlash(undefined)}>
          {flash}
        </Alert>
      )}
      {isInvalidCredentials && (
        <Alert variant="danger" className="mt-3">
          {t('login.invalidCredentials')}
        </Alert>
      )}
      {isGenericError && (
        <Alert variant="danger" className="mt-3">
          {t('errors.generic')}
        </Alert>
      )}

      <Form className="mt-4" onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <div className="mb-3">
          <FormLabel>
            {t('login.emailLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <div className="app-search">
            <FormControl
              type="email"
              placeholder={t('login.emailPlaceholder')}
              autoComplete="email"
              isInvalid={!!form.formState.errors.email}
              {...form.register('email')}
            />
            <Icon icon="mail" className="app-search-icon text-muted" />
          </div>
          {form.formState.errors.email && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.email.message ?? '')}</div>}
        </div>

        <div className="mb-3">
          <FormLabel>
            {t('login.passwordLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <div className="app-search">
            <FormControl
              type="password"
              placeholder={t('login.passwordPlaceholder')}
              autoComplete="current-password"
              isInvalid={!!form.formState.errors.password}
              {...form.register('password')}
            />
            <Icon icon="lock-keyhole" className="app-search-icon text-muted" />
          </div>
          {form.formState.errors.password && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.password.message ?? '')}</div>}
        </div>

        <div className="d-grid">
          <Button variant="primary" type="submit" className="btn fw-bold py-2" disabled={loginMutation.isPending}>
            {loginMutation.isPending && <Spinner as="span" animation="border" size="sm" className="me-2" />}
            {t('login.submit')}
          </Button>
        </div>
      </Form>
    </>
  )
}

export default LoginPage
