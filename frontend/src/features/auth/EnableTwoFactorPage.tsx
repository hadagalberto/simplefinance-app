import { OtpCodeInput } from '@/features/auth/OtpCodeInput'
import Icon from '@/components/Icon'
import { zodResolver } from '@hookform/resolvers/zod'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { Alert, Button, Col, Form, FormCheck, FormLabel, Row, Spinner } from 'react-bootstrap'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'

import { useActivateTwoFactor, useEnableTwoFactor } from '@/features/auth/api'
import { twoFactorVerifySchema, type TwoFactorVerifyFormValues } from '@/features/auth/schemas'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'
import { DocumentTitle } from '@/shell/DocumentTitle'

type Step = 'qr' | 'verify' | 'recovery-codes'

/**
 * Signed-in account screen, rendered inside AuthLayout behind ProtectedRoute.
 * 3 steps: (1) fetch /2fa/enable on mount, show QR + manual secret; (2)
 * verify a 6-digit code via /2fa/activate; (3) show the 8 recovery codes
 * exactly once, gated behind a "I've saved these codes" checkbox. Recovery
 * codes never touch localStorage/query-cache retention beyond this page's
 * own component state (T-10-09).
 */
const EnableTwoFactorPage = () => {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const { accessToken } = useAuth()
  const enableMutation = useEnableTwoFactor()
  const activateMutation = useActivateTwoFactor()

  const [step, setStep] = useState<Step>('qr')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [codesSaved, setCodesSaved] = useState(false)
  const [digits, setDigits] = useState<string[]>(Array(6).fill(''))

  const form = useForm<TwoFactorVerifyFormValues>({
    resolver: zodResolver(twoFactorVerifySchema),
    defaultValues: { code: '' },
  })

  useEffect(() => {
    if (!accessToken) return
    enableMutation.mutate({ accessToken })
    // Only ever fetched once on mount — the secret/QR don't change across this page's lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accessToken])

  const activateErrorCopy = (() => {
    if (!activateMutation.isError) return null
    const error = activateMutation.error
    if (error instanceof ApiError && error.status === 400) return t('enable2fa.verify.invalidCode')
    return t('errors.generic')
  })()

  function handleDigitsChange(next: string[]) {
    setDigits(next)
    form.setValue('code', next.join(''), { shouldValidate: form.formState.isSubmitted })
  }

  async function onVerifySubmit(values: TwoFactorVerifyFormValues) {
    if (!accessToken) return
    try {
      const response = await activateMutation.mutateAsync({ accessToken, code: values.code })
      setRecoveryCodes(response.recoveryCodes)
      setStep('recovery-codes')
    } catch {
      // Surfaced via activateErrorCopy above; the user can retry Step 2 without restarting Step 1.
    }
  }

  async function copyCodes() {
    if (!recoveryCodes) return
    try {
      await navigator.clipboard.writeText(recoveryCodes.join('\n'))
    } catch {
      // Clipboard write failures are non-fatal — the download button remains available.
    }
  }

  function downloadCodes() {
    if (!recoveryCodes) return
    const blob = new Blob([recoveryCodes.join('\n') + '\n'], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = 'simplefinance-recovery-codes.txt'
    document.body.appendChild(link)
    link.click()
    document.body.removeChild(link)
    URL.revokeObjectURL(url)
  }

  function finish() {
    navigate('/')
  }

  return (
    <>
      <DocumentTitle title={t('enable2fa.pageTitle')} />
      <p className="sf-auth__eyebrow">{t('enable2fa.pageSubtitle')}</p>
      <h1 className="sf-auth__title mb-4">{t('enable2fa.pageTitle')}</h1>

      {step === 'qr' && (
        <>
          {enableMutation.isPending && (
            <div className="d-flex align-items-center gap-2 text-muted">
              <Spinner animation="border" size="sm" />
              {t('enable2fa.qr.generating')}
            </div>
          )}
          {enableMutation.isError && (
            <Alert variant="danger">{t('errors.generic')}</Alert>
          )}
          {enableMutation.data && (
            <>
              <p className="text-muted">
                {t('enable2fa.qr.scanInstructions')}
              </p>
              <div className="text-center mb-3">
                <img
                  src={`data:image/png;base64,${enableMutation.data.qrCodePngBase64}`}
                  alt={t('enable2fa.qr.qrCodeAlt')}
                  style={{ width: '192px', height: '192px' }}
                  className="rounded border"
                />
              </div>
              <div className="mb-3">
                <FormLabel htmlFor="totp-secret">{t('enable2fa.qr.manualEntryLabel')}</FormLabel>
                <input id="totp-secret" readOnly value={enableMutation.data.secret} className="form-control" />
              </div>
              <div className="d-grid">
                <Button variant="primary" onClick={() => setStep('verify')}>
                  {t('enable2fa.qr.continue')}
                </Button>
              </div>
            </>
          )}
        </>
      )}

      {step === 'verify' && (
        <>
          {activateErrorCopy && <Alert variant="danger">{activateErrorCopy}</Alert>}
          <Form onSubmit={form.handleSubmit(onVerifySubmit)} noValidate>
            <div className="mb-3">
              <OtpCodeInput code={digits} setCode={handleDigitsChange} label={t('enable2fa.verify.otpLabel')} />
              {form.formState.errors.code && (
                <div className="text-danger fs-xs mt-1">{t(form.formState.errors.code.message ?? '')}</div>
              )}
            </div>
            <div className="d-grid">
              <Button variant="primary" type="submit" disabled={activateMutation.isPending}>
                {activateMutation.isPending && <Spinner as="span" animation="border" size="sm" className="me-2" />}
                {t('enable2fa.verify.submit')}
              </Button>
            </div>
          </Form>
        </>
      )}

      {step === 'recovery-codes' && recoveryCodes && (
        <>
          <FormLabel>{t('enable2fa.recoveryCodes.label')}</FormLabel>
          <p className="text-muted">
            {t('enable2fa.recoveryCodes.description')}
          </p>
          <Row className="row-cols-2 g-2 border rounded bg-light-subtle p-3 mb-3 font-monospace">
            {recoveryCodes.map((code) => (
              <Col key={code}>{code}</Col>
            ))}
          </Row>
          <div className="d-flex gap-2 mb-3">
            <Button type="button" variant="outline-secondary" size="sm" onClick={copyCodes}>
              <Icon icon="copy" className="me-1" />
              {t('enable2fa.recoveryCodes.copy')}
            </Button>
            <Button type="button" variant="outline-secondary" size="sm" onClick={downloadCodes}>
              <Icon icon="download" className="me-1" />
              {t('enable2fa.recoveryCodes.download')}
            </Button>
          </div>
          <FormCheck className="mb-3">
            <Form.Check.Input
              type="checkbox"
              id="codesSaved"
              checked={codesSaved}
              onChange={(e) => setCodesSaved(e.target.checked)}
            />
            <Form.Check.Label htmlFor="codesSaved">{t('enable2fa.recoveryCodes.savedCheckbox')}</Form.Check.Label>
          </FormCheck>
          <div className="d-grid">
            <Button variant="primary" disabled={!codesSaved} onClick={finish}>
              {t('enable2fa.recoveryCodes.continue')}
            </Button>
          </div>
        </>
      )}
    </>
  )
}

export default EnableTwoFactorPage
