import { PasswordStrengthField } from '@/features/auth/PasswordStrengthField'
import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Alert, Button, Form, FormControl, FormLabel, Spinner } from 'react-bootstrap'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'

import { useChangePassword } from '@/features/auth/api'
import { changePasswordSchema, type ChangePasswordFormValues } from '@/features/auth/schemas'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'
import { DocumentTitle } from '@/shell/DocumentTitle'

/**
 * Signed-in account screen, rendered inside AuthLayout behind ProtectedRoute
 * (same as EnableTwoFactorPage). On success the backend has already revoked
 * every refresh token for this user (T-10-10), so the frontend must not
 * pretend the current session is still valid: calls AuthContext.logout()
 * then redirects to /login.
 */
const ChangePasswordPage = () => {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const { accessToken, logout } = useAuth()
  const changePasswordMutation = useChangePassword()
  const [newPassword, setNewPassword] = useState('')

  const form = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordSchema),
    defaultValues: { currentPassword: '', newPassword: '', confirmNewPassword: '' },
  })

  const isInvalidCurrentPassword =
    changePasswordMutation.isError &&
    changePasswordMutation.error instanceof ApiError &&
    changePasswordMutation.error.status === 400 &&
    changePasswordMutation.error.error === 'invalid_current_password'

  const isGenericError = changePasswordMutation.isError && !isInvalidCurrentPassword

  async function onSubmit(values: ChangePasswordFormValues) {
    if (!accessToken) return

    if (isInvalidCurrentPassword) {
      changePasswordMutation.reset()
    }

    try {
      await changePasswordMutation.mutateAsync({
        accessToken,
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      })
      await logout()
      navigate('/login')
    } catch (error) {
      if (error instanceof ApiError && error.status === 400 && error.error === 'invalid_current_password') {
        form.setError('currentPassword', { type: 'server', message: t('changePassword.invalidCurrentPassword') })
      }
      // Any other failure is surfaced via isGenericError below.
    }
  }

  return (
    <>
      <DocumentTitle title={t('changePassword.pageTitle')} />
      <p className="sf-auth__eyebrow">{t('changePassword.pageSubtitle')}</p>
      <h1 className="sf-auth__title mb-4">{t('changePassword.pageTitle')}</h1>

      {isGenericError && <Alert variant="danger">{t('errors.generic')}</Alert>}

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <div className="mb-3">
          <FormLabel>
            {t('changePassword.currentPasswordLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <FormControl
            type="password"
            autoComplete="current-password"
            isInvalid={!!form.formState.errors.currentPassword}
            {...form.register('currentPassword')}
          />
          {form.formState.errors.currentPassword && (
            <div className="text-danger fs-xs mt-1">{t(form.formState.errors.currentPassword.message ?? '')}</div>
          )}
        </div>

        <div className="mb-3" data-password="bar">
          <FormLabel>
            {t('changePassword.newPasswordLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <PasswordStrengthField
            name="newPassword"
            password={newPassword}
            setPassword={(value) => {
              setNewPassword(value)
              form.setValue('newPassword', value, { shouldValidate: form.formState.isSubmitted })
            }}
            placeholder={t('changePassword.newPasswordPlaceholder')}
            inputClassName="form-control"
            showIcon
          />
          {form.formState.errors.newPassword && (
            <div className="text-danger fs-xs mt-1">{t(form.formState.errors.newPassword.message ?? '')}</div>
          )}
        </div>

        <div className="mb-3">
          <FormLabel>
            {t('changePassword.confirmNewPasswordLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <FormControl
            type="password"
            autoComplete="new-password"
            isInvalid={!!form.formState.errors.confirmNewPassword}
            {...form.register('confirmNewPassword')}
          />
          {form.formState.errors.confirmNewPassword && (
            <div className="text-danger fs-xs mt-1">{t(form.formState.errors.confirmNewPassword.message ?? '')}</div>
          )}
        </div>

        <p className="text-muted fs-xs">{t('changePassword.signOutNotice')}</p>

        <div className="d-grid">
          <Button variant="primary" type="submit" disabled={changePasswordMutation.isPending}>
            {changePasswordMutation.isPending && <Spinner as="span" animation="border" size="sm" className="me-2" />}
            {t('changePassword.submit')}
          </Button>
        </div>
      </Form>
    </>
  )
}

export default ChangePasswordPage
