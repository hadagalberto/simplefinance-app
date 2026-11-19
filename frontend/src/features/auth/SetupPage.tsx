import { PasswordStrengthField } from '@/features/auth/PasswordStrengthField'
import Icon from '@/components/Icon'
import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm, type FieldPath } from 'react-hook-form'
import { Alert, Button, Form, FormControl, FormLabel, Spinner } from 'react-bootstrap'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'

import { useSetup } from '@/features/auth/api'
import { setupSchema, type SetupFormValues } from '@/features/auth/schemas'
import { ApiError } from '@/lib/apiClient'
import { DocumentTitle } from '@/shell/DocumentTitle'

/**
 * First-run account creation screen, rendered inside AuthLayout and wired to
 * the useSetup mutation. Setup never signs the user in (SetupResponse carries
 * no session), so success always navigates to /login.
 */
const SetupPage = () => {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const setupMutation = useSetup()
  const [password, setPassword] = useState('')

  const form = useForm<SetupFormValues>({
    resolver: zodResolver(setupSchema),
    defaultValues: { name: '', email: '', password: '' },
  })

  const generalError =
    setupMutation.isError &&
    !(setupMutation.error instanceof ApiError && setupMutation.error.status === 409) &&
    !(setupMutation.error instanceof ApiError && setupMutation.error.errors)

  async function onSubmit(values: SetupFormValues) {
    try {
      await setupMutation.mutateAsync(values)
      navigate('/login')
    } catch (error) {
      if (error instanceof ApiError) {
        if (error.status === 409 && error.error === 'setup_already_completed') {
          navigate('/login', { state: { flash: t('setup.alreadyCompleteFlash') } })
          return
        }

        if (error.status === 400 && error.errors) {
          for (const [field, messages] of Object.entries(error.errors)) {
            form.setError(field as FieldPath<SetupFormValues>, {
              type: 'server',
              message: messages[0],
            })
          }
        }
      }
      // Other statuses (network failure, 500, etc.) fall through to the generic Alert below.
    }
  }

  return (
    <>
      <DocumentTitle title={t('setup.title')} />
      <h1 className="sf-auth__title">{t('setup.title')}</h1>
      <p className="sf-auth__subtitle">{t('setup.subtitle')}</p>

      {generalError && (
        <Alert variant="danger" className="mt-3">
          {t('errors.generic')}
        </Alert>
      )}

      <Form className="mt-4" onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <div className="mb-3">
          <FormLabel>
            {t('setup.fullNameLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <div className="app-search">
            <FormControl
              type="text"
              placeholder={t('setup.fullNamePlaceholder')}
              autoComplete="name"
              isInvalid={!!form.formState.errors.name}
              {...form.register('name')}
            />
            <Icon icon="circle-user-round" className="app-search-icon text-muted" />
          </div>
          {form.formState.errors.name && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.name.message ?? '')}</div>}
        </div>

        <div className="mb-3">
          <FormLabel>
            {t('setup.emailLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <div className="app-search">
            <FormControl
              type="email"
              placeholder={t('setup.emailPlaceholder')}
              autoComplete="email"
              isInvalid={!!form.formState.errors.email}
              {...form.register('email')}
            />
            <Icon icon="mail" className="app-search-icon text-muted" />
          </div>
          {form.formState.errors.email && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.email.message ?? '')}</div>}
        </div>

        <div className="mb-3" data-password="bar">
          <FormLabel>
            {t('setup.passwordLabel')}&nbsp;
            <span className="text-danger">*</span>
          </FormLabel>
          <PasswordStrengthField
            name="password"
            password={password}
            setPassword={(value) => {
              setPassword(value)
              form.setValue('password', value, { shouldValidate: form.formState.isSubmitted })
            }}
            placeholder={t('setup.passwordPlaceholder')}
            inputClassName="form-control"
            showIcon
          />
          {form.formState.errors.password && <div className="text-danger fs-xs mt-1">{t(form.formState.errors.password.message ?? '')}</div>}
        </div>

        <div className="d-grid">
          <Button variant="primary" type="submit" className="fw-semibold py-2" disabled={setupMutation.isPending}>
            {setupMutation.isPending && <Spinner as="span" animation="border" size="sm" className="me-2" />}
            {t('setup.submit')}
          </Button>
        </div>
      </Form>
    </>
  )
}

export default SetupPage
