import { useEffect } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { Alert, Button, Card, CardBody, CardHeader, CardTitle, Form, FormControl, FormLabel, Placeholder } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import type { TFunction } from 'i18next'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'
import { useTheme } from '@/shell/theme/useTheme'

import { usePreferencesQuery, useUpdatePreferencesMutation, type PreferencesResponse } from './api'

function buildPreferencesFormSchema(t: TFunction<'preferences'>) {
  return z.object({
    timezone: z.string().min(1, t('timezone.required')),
    baseCurrency: z.string().min(3, t('baseCurrency.invalid')).max(3, t('baseCurrency.invalid')),
    syncCronExpression: z.string().min(1, t('syncCron.required')),
    defaultTheme: z.enum(['light', 'dark']),
  })
}

type PreferencesFormValues = z.infer<ReturnType<typeof buildPreferencesFormSchema>>

function FormSkeleton() {
  const { t } = useTranslation('preferences')

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h4">{t('title')}</CardTitle>
        <p className="text-muted mb-0">{t('subtitle')}</p>
      </CardHeader>
      <CardBody className="d-flex flex-column gap-3">
        <Placeholder as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 38 }} />
        </Placeholder>
        <Placeholder as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 38 }} />
        </Placeholder>
        <Placeholder as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 38 }} />
        </Placeholder>
        <Placeholder as="div" animation="glow">
          <Placeholder xs={12} style={{ height: 38 }} />
        </Placeholder>
      </CardBody>
    </Card>
  )
}

/**
 * Settings > Preferences screen (FE-PREF-01): timezone / base currency /
 * sync schedule / default theme, backed by v1.0 Phase 8's unchanged
 * GET/PUT /api/preferences. On save, the form is repopulated from the PUT
 * response (not just the locally-entered values, proving the round-trip)
 * and, when the saved defaultTheme differs from the current theme
 * preference, calls useTheme().setTheme(defaultTheme) so the change is
 * visible live. Dark remains the app's boot default; this screen only
 * changes the stored server preference and the current theme.
 */
export function PreferencesPage() {
  const { t } = useTranslation('preferences')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { theme, setTheme } = useTheme()
  const { showNotification } = useNotificationContext()

  const preferencesQuery = usePreferencesQuery(token)
  const updateMutation = useUpdatePreferencesMutation()

  const form = useForm<PreferencesFormValues>({
    resolver: zodResolver(buildPreferencesFormSchema(t)),
    defaultValues: {
      timezone: '',
      baseCurrency: '',
      syncCronExpression: '',
      defaultTheme: 'light',
    },
  })

  useEffect(() => {
    if (preferencesQuery.data) {
      form.reset(preferencesQuery.data)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preferencesQuery.data])

  async function onSubmit(values: PreferencesFormValues) {
    try {
      const updated: PreferencesResponse = await updateMutation.mutateAsync({
        accessToken: token,
        ...values,
      })
      form.reset(updated)
      if (updated.defaultTheme !== theme) {
        setTheme(updated.defaultTheme)
      }
      showNotification({ message: t('saveSuccess'), variant: 'success' })
    } catch (error) {
      const message = error instanceof ApiError && error.error ? error.error : t('saveError')
      showNotification({ message, variant: 'danger' })
    }
  }

  return (
    <div style={{ maxWidth: 480 }}>
      {preferencesQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void preferencesQuery.refetch()}>
            {t('retry')}
          </Button>
        </Alert>
      )}

      {preferencesQuery.isPending && <FormSkeleton />}

      {!preferencesQuery.isPending && !preferencesQuery.isError && (
        <Card>
          <CardHeader>
            <CardTitle as="h4">{t('title')}</CardTitle>
            <p className="text-muted mb-0">{t('subtitle')}</p>
          </CardHeader>
          <CardBody>
            <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
              <div className="mb-3">
                <FormLabel>{t('timezone.label')}</FormLabel>
                <FormControl
                  placeholder={t('timezone.placeholder')}
                  isInvalid={!!form.formState.errors.timezone}
                  {...form.register('timezone')}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('timezone.helper')}</p>
                {form.formState.errors.timezone && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.timezone.message}</div>
                )}
              </div>

              <div className="mb-3">
                <FormLabel>{t('baseCurrency.label')}</FormLabel>
                <FormControl
                  placeholder={t('baseCurrency.placeholder')}
                  maxLength={3}
                  isInvalid={!!form.formState.errors.baseCurrency}
                  {...form.register('baseCurrency')}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('baseCurrency.helper')}</p>
                {form.formState.errors.baseCurrency && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.baseCurrency.message}</div>
                )}
              </div>

              <div className="mb-3">
                <FormLabel>{t('syncCron.label')}</FormLabel>
                <FormControl
                  placeholder={t('syncCron.placeholder')}
                  isInvalid={!!form.formState.errors.syncCronExpression}
                  {...form.register('syncCronExpression')}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('syncCron.helper')}</p>
                {form.formState.errors.syncCronExpression && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.syncCronExpression.message}</div>
                )}
              </div>

              <div className="mb-3">
                <FormLabel>{t('defaultTheme.label')}</FormLabel>
                <Form.Select {...form.register('defaultTheme')}>
                  <option value="light">{t('defaultTheme.light')}</option>
                  <option value="dark">{t('defaultTheme.dark')}</option>
                </Form.Select>
                {form.formState.errors.defaultTheme && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.defaultTheme.message}</div>
                )}
              </div>

              <Button type="submit" variant="primary" disabled={updateMutation.isPending}>
                {t('save')}
              </Button>
            </Form>
          </CardBody>
        </Card>
      )}
    </div>
  )
}
