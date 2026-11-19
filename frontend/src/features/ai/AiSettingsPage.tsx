import { useEffect, useMemo, useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CardTitle,
  Form,
  FormCheck,
  FormControl,
  FormLabel,
  Placeholder,
  ProgressBar,
} from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { amountValueAs } from '@/lib/amount'
import type { TFunction } from 'i18next'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'

import { useAiSettings, useAiUsage, useUpdateAiSettings, type AiSettingsResponse } from './api'

// Suggestions only — the model fields are free text. The backend picks the provider from the name:
// claude-* → Anthropic, vendor/model slug → OpenRouter, anything else → DeepSeek.
const MODEL_SUGGESTIONS = [
  'deepseek-chat',
  'deepseek-reasoner',
  'claude-haiku-5-5',
  'claude-sonnet-5-5',
  'claude-opus-5-5',
] as const

// The six per-feature toggles, in display order. Labels resolve from the `ai` namespace.
const FEATURE_TOGGLES = [
  'aiCategorizationEnabled',
  'aiAssistantEnabled',
  'aiInsightsEnabled',
  'aiAnomalyEnabled',
  'aiForecastEnabled',
  'aiMonthlySummaryEnabled',
] as const

function buildAiFormSchema(t: TFunction<'ai'>) {
  return z.object({
    aiEnabled: z.boolean(),
    aiCategorizationEnabled: z.boolean(),
    aiAssistantEnabled: z.boolean(),
    aiInsightsEnabled: z.boolean(),
    aiAnomalyEnabled: z.boolean(),
    aiForecastEnabled: z.boolean(),
    aiMonthlySummaryEnabled: z.boolean(),
    aiAutoApplyThreshold: z
      .number({ message: t('threshold.invalid') })
      .min(0, t('threshold.invalid'))
      .max(1, t('threshold.invalid')),
    aiMonthlyBudgetUsd: z
      .number({ message: t('budget.invalid') })
      .min(0, t('budget.invalid')),
    aiChatModel: z.string().trim().min(1, t('models.required')).max(64, t('models.required')),
    aiSummaryModel: z.string().trim().min(1, t('models.required')).max(64, t('models.required')),
    aiLanguage: z.string().min(1, t('language.required')),
  })
}

type AiFormValues = z.infer<ReturnType<typeof buildAiFormSchema>>

const DEFAULT_VALUES: AiFormValues = {
  aiEnabled: false,
  aiCategorizationEnabled: false,
  aiAssistantEnabled: false,
  aiInsightsEnabled: false,
  aiAnomalyEnabled: false,
  aiForecastEnabled: false,
  aiMonthlySummaryEnabled: false,
  aiAutoApplyThreshold: 0.85,
  aiMonthlyBudgetUsd: 3,
  aiChatModel: 'deepseek-chat',
  aiSummaryModel: 'deepseek-reasoner',
  aiLanguage: 'pt-BR',
}

function toFormValues(data: AiSettingsResponse): AiFormValues {
  return {
    aiEnabled: data.aiEnabled,
    aiCategorizationEnabled: data.aiCategorizationEnabled,
    aiAssistantEnabled: data.aiAssistantEnabled,
    aiInsightsEnabled: data.aiInsightsEnabled,
    aiAnomalyEnabled: data.aiAnomalyEnabled,
    aiForecastEnabled: data.aiForecastEnabled,
    aiMonthlySummaryEnabled: data.aiMonthlySummaryEnabled,
    aiAutoApplyThreshold: data.aiAutoApplyThreshold,
    aiMonthlyBudgetUsd: data.aiMonthlyBudgetUsd,
    aiChatModel: data.aiChatModel,
    aiSummaryModel: data.aiSummaryModel,
    aiLanguage: data.aiLanguage,
  }
}

function currentMonth(): string {
  const now = new Date()
  return `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, '0')}`
}

function FormSkeleton() {
  const { t } = useTranslation('ai')

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h4">{t('title')}</CardTitle>
        <p className="text-muted mb-0">{t('subtitle')}</p>
      </CardHeader>
      <CardBody className="d-flex flex-column gap-3">
        {Array.from({ length: 6 }).map((_, i) => (
          <Placeholder key={i} as="div" animation="glow">
            <Placeholder xs={12} style={{ height: 32 }} />
          </Placeholder>
        ))}
      </CardBody>
    </Card>
  )
}

/**
 * A single write-only provider-key editor. Renders a password input (the actual key is never sent
 * TO the browser — only the `present` flag is), a present/absent badge, and a "Limpar" affordance
 * that stages an explicit clear (revert to the env fallback). Placeholder reflects current state.
 */
function KeyField({
  label,
  present,
  value,
  cleared,
  onChange,
  onClear,
}: {
  label: string
  present: boolean
  value: string
  cleared: boolean
  onChange: (v: string) => void
  onClear: () => void
}) {
  const { t } = useTranslation('ai')
  const placeholder = present ? t('keys.configuredPlaceholder') : t('keys.absentPlaceholder')

  return (
    <div className="border rounded px-3 py-2 d-flex flex-column gap-1">
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-2">
        <FormLabel className="mb-0">{label}</FormLabel>
        <Badge bg={present && !cleared ? 'success' : 'secondary'}>
          {present && !cleared ? t('keys.present') : t('keys.absent')}
        </Badge>
      </div>
      <div className="d-flex gap-2 align-items-center">
        <FormControl
          type="password"
          autoComplete="off"
          placeholder={cleared ? t('keys.willClear') : placeholder}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        {present && (
          <Button type="button" variant="outline-secondary" size="sm" onClick={onClear} disabled={cleared}>
            {t('keys.clear')}
          </Button>
        )}
      </div>
    </div>
  )
}

function formatUsd(value: number): string {
  return `US$ ${value.toFixed(2)}`
}

/**
 * AI-FND-07/08: Settings > IA screen. Master + per-feature toggles, the auto-apply confidence
 * threshold, the hard monthly USD cap, free-text per-feature model names (the provider is routed from
 * the name), and write-only key editors with key-present badges (DeepSeek/OpenAI/Anthropic/OpenRouter --
 * presence only, the key value is never sent to the browser). Plus a
 * read-only Usage panel showing month-to-date spend/tokens per feature and a spend-vs-cap bar.
 * Backed by GET/PUT /api/ai/settings and GET /api/ai/usage. Copies the preferences feature's shape.
 */
export function AiSettingsPage() {
  const { t } = useTranslation('ai')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const month = useMemo(() => currentMonth(), [])
  const settingsQuery = useAiSettings(token)
  const usageQuery = useAiUsage(token, month)
  const updateMutation = useUpdateAiSettings()

  // Write-only key inputs live OUTSIDE the settings form: the API never returns the key value, so we
  // only ever SEND them. Empty input + no clear-intent ⇒ field omitted (leave unchanged); a typed
  // value ⇒ set; the "Limpar" button ⇒ send "" to clear (revert to the env fallback).
  const [deepSeekKeyInput, setDeepSeekKeyInput] = useState('')
  const [openAiKeyInput, setOpenAiKeyInput] = useState('')
  const [anthropicKeyInput, setAnthropicKeyInput] = useState('')
  const [openRouterKeyInput, setOpenRouterKeyInput] = useState('')
  const [clearDeepSeek, setClearDeepSeek] = useState(false)
  const [clearOpenAi, setClearOpenAi] = useState(false)
  const [clearAnthropic, setClearAnthropic] = useState(false)
  const [clearOpenRouter, setClearOpenRouter] = useState(false)

  const form = useForm<AiFormValues>({
    resolver: zodResolver(buildAiFormSchema(t)),
    defaultValues: DEFAULT_VALUES,
  })

  const thresholdValue = useWatch({ control: form.control, name: 'aiAutoApplyThreshold' })

  useEffect(() => {
    if (settingsQuery.data) {
      form.reset(toFormValues(settingsQuery.data))
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settingsQuery.data])

  function resetKeyInputs() {
    setDeepSeekKeyInput('')
    setOpenAiKeyInput('')
    setAnthropicKeyInput('')
    setOpenRouterKeyInput('')
    setClearDeepSeek(false)
    setClearOpenAi(false)
    setClearAnthropic(false)
    setClearOpenRouter(false)
  }

  async function onSubmit(values: AiFormValues) {
    // Tri-state per key: clear-intent ⇒ "", typed value ⇒ that value, otherwise omit the field.
    const deepSeekApiKey = clearDeepSeek ? '' : deepSeekKeyInput.trim() !== '' ? deepSeekKeyInput : undefined
    const openAiApiKey = clearOpenAi ? '' : openAiKeyInput.trim() !== '' ? openAiKeyInput : undefined
    const anthropicApiKey = clearAnthropic ? '' : anthropicKeyInput.trim() !== '' ? anthropicKeyInput : undefined
    const openRouterApiKey = clearOpenRouter ? '' : openRouterKeyInput.trim() !== '' ? openRouterKeyInput : undefined

    try {
      const updated = await updateMutation.mutateAsync({
        accessToken: token,
        ...values,
        ...(deepSeekApiKey !== undefined ? { deepSeekApiKey } : {}),
        ...(openAiApiKey !== undefined ? { openAiApiKey } : {}),
        ...(anthropicApiKey !== undefined ? { anthropicApiKey } : {}),
        ...(openRouterApiKey !== undefined ? { openRouterApiKey } : {}),
      })
      form.reset(toFormValues(updated))
      resetKeyInputs()
      showNotification({ message: t('saveSuccess'), variant: 'success' })
    } catch (error) {
      const message = error instanceof ApiError && error.error ? error.error : t('saveError')
      showNotification({ message, variant: 'danger' })
    }
  }

  const usage = usageQuery.data
  const spendPct = usage && usage.monthlyBudgetUsd > 0
    ? Math.min(100, (usage.totalCostUsd / usage.monthlyBudgetUsd) * 100)
    : 0

  return (
    <div style={{ maxWidth: 640 }} className="d-flex flex-column gap-3">
      {settingsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void settingsQuery.refetch()}>
            {t('retry')}
          </Button>
        </Alert>
      )}

      {settingsQuery.isPending && <FormSkeleton />}

      {!settingsQuery.isPending && !settingsQuery.isError && (
        <Card>
          <CardHeader>
            <CardTitle as="h4">{t('title')}</CardTitle>
            <p className="text-muted mb-0">{t('subtitle')}</p>
          </CardHeader>
          <CardBody>
            <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
              <div className="mb-3">
                <FormCheck
                  type="switch"
                  id="aiEnabled"
                  label={t('master.label')}
                  {...form.register('aiEnabled')}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('master.helper')}</p>
              </div>

              <hr />

              <FormLabel className="fw-semibold">{t('features.label')}</FormLabel>
              <div className="mb-3 d-flex flex-column gap-2">
                {FEATURE_TOGGLES.map((name) => (
                  <FormCheck
                    key={name}
                    type="switch"
                    id={name}
                    label={t(`features.${name}`)}
                    {...form.register(name)}
                  />
                ))}
              </div>

              <hr />

              <div className="mb-3">
                <FormLabel>
                  {t('threshold.label')}: {thresholdValue}
                </FormLabel>
                <Form.Range
                  min={0}
                  max={1}
                  step={0.01}
                  {...form.register('aiAutoApplyThreshold', { valueAsNumber: true })}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('threshold.helper')}</p>
                {form.formState.errors.aiAutoApplyThreshold && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.aiAutoApplyThreshold.message}</div>
                )}
              </div>

              <div className="mb-3">
                <FormLabel>{t('budget.label')}</FormLabel>
                <FormControl
                  type="text"
                  inputMode="decimal"
                  autoComplete="off"
                  isInvalid={!!form.formState.errors.aiMonthlyBudgetUsd}
                  {...form.register('aiMonthlyBudgetUsd', { setValueAs: amountValueAs })}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('budget.helper')}</p>
                {form.formState.errors.aiMonthlyBudgetUsd && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.aiMonthlyBudgetUsd.message}</div>
                )}
              </div>

              <hr />

              <datalist id="ai-model-suggestions">
                {MODEL_SUGGESTIONS.map((m) => (
                  <option key={m} value={m} />
                ))}
              </datalist>

              <div className="mb-3">
                <FormLabel htmlFor="aiChatModel">{t('models.chat')}</FormLabel>
                <FormControl
                  id="aiChatModel"
                  type="text"
                  list="ai-model-suggestions"
                  autoComplete="off"
                  spellCheck={false}
                  isInvalid={!!form.formState.errors.aiChatModel}
                  {...form.register('aiChatModel')}
                />
                {form.formState.errors.aiChatModel && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.aiChatModel.message}</div>
                )}
              </div>

              <div className="mb-3">
                <FormLabel htmlFor="aiSummaryModel">{t('models.summary')}</FormLabel>
                <FormControl
                  id="aiSummaryModel"
                  type="text"
                  list="ai-model-suggestions"
                  autoComplete="off"
                  spellCheck={false}
                  isInvalid={!!form.formState.errors.aiSummaryModel}
                  {...form.register('aiSummaryModel')}
                />
                {form.formState.errors.aiSummaryModel && (
                  <div className="text-danger fs-xs mt-1">{form.formState.errors.aiSummaryModel.message}</div>
                )}
                <p className="text-muted fs-xs mt-1 mb-0">{t('models.helper')}</p>
              </div>

              <div className="mb-3">
                <FormLabel>{t('language.label')}</FormLabel>
                <Form.Select {...form.register('aiLanguage')}>
                  <option value="pt-BR">pt-BR</option>
                  <option value="en">en</option>
                </Form.Select>
              </div>

              <hr />

              <FormLabel className="fw-semibold">{t('keys.label')}</FormLabel>
              <div className="mb-3 d-flex flex-column gap-2">
                <KeyField
                  label={t('keys.deepSeek')}
                  present={settingsQuery.data?.deepSeekKeyPresent ?? false}
                  value={deepSeekKeyInput}
                  cleared={clearDeepSeek}
                  onChange={(v) => {
                    setDeepSeekKeyInput(v)
                    setClearDeepSeek(false)
                  }}
                  onClear={() => {
                    setDeepSeekKeyInput('')
                    setClearDeepSeek(true)
                  }}
                />
                <KeyField
                  label={t('keys.openAi')}
                  present={settingsQuery.data?.openAiKeyPresent ?? false}
                  value={openAiKeyInput}
                  cleared={clearOpenAi}
                  onChange={(v) => {
                    setOpenAiKeyInput(v)
                    setClearOpenAi(false)
                  }}
                  onClear={() => {
                    setOpenAiKeyInput('')
                    setClearOpenAi(true)
                  }}
                />
                <KeyField
                  label={t('keys.anthropic')}
                  present={settingsQuery.data?.anthropicKeyPresent ?? false}
                  value={anthropicKeyInput}
                  cleared={clearAnthropic}
                  onChange={(v) => {
                    setAnthropicKeyInput(v)
                    setClearAnthropic(false)
                  }}
                  onClear={() => {
                    setAnthropicKeyInput('')
                    setClearAnthropic(true)
                  }}
                />
                <KeyField
                  label={t('keys.openRouter')}
                  present={settingsQuery.data?.openRouterKeyPresent ?? false}
                  value={openRouterKeyInput}
                  cleared={clearOpenRouter}
                  onChange={(v) => {
                    setOpenRouterKeyInput(v)
                    setClearOpenRouter(false)
                  }}
                  onClear={() => {
                    setOpenRouterKeyInput('')
                    setClearOpenRouter(true)
                  }}
                />
                <p className="text-muted fs-xs mt-1 mb-0">{t('keys.helper')}</p>
              </div>

              <Button type="submit" variant="primary" disabled={updateMutation.isPending}>
                {t('save')}
              </Button>
            </Form>
          </CardBody>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle as="h4">{t('usage.title')}</CardTitle>
          <p className="text-muted mb-0">{t('usage.subtitle', { month })}</p>
        </CardHeader>
        <CardBody>
          {usageQuery.isPending && (
            <Placeholder as="div" animation="glow">
              <Placeholder xs={12} style={{ height: 32 }} />
            </Placeholder>
          )}

          {usageQuery.isError && <div className="text-muted">{t('usage.loadError')}</div>}

          {usage && (
            <>
              <div className="d-flex justify-content-between mb-1">
                <span>{t('usage.spend')}</span>
                <span className="fw-semibold">
                  {formatUsd(usage.totalCostUsd)} / {formatUsd(usage.monthlyBudgetUsd)}
                </span>
              </div>
              <ProgressBar
                now={spendPct}
                variant={spendPct >= 100 ? 'danger' : spendPct >= 80 ? 'warning' : 'success'}
                className="mb-3"
              />

              {usage.features.length === 0 ? (
                <div className="text-muted">{t('usage.empty')}</div>
              ) : (
                <div className="table-responsive">
                  <table className="table table-sm mb-0">
                    <thead>
                      <tr>
                        <th>{t('usage.feature')}</th>
                        <th className="text-end">{t('usage.tokens')}</th>
                        <th className="text-end">{t('usage.cost')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {usage.features.map((f) => (
                        <tr key={f.feature}>
                          <td>{f.feature}</td>
                          <td className="text-end">{(f.promptTokens + f.completionTokens).toLocaleString()}</td>
                          <td className="text-end">{formatUsd(f.estimatedCostUsd)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </CardBody>
      </Card>
    </div>
  )
}
