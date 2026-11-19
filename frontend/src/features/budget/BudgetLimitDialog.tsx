import { useEffect, useMemo } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { Button, Form, FormControl, FormLabel, Modal } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'
import { amountValueAs } from '@/lib/amount'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'

import { useUpsertBudgetMutation } from './api'

/**
 * Client-side mirror of 05-05's server-side `LimitAmount > 0` rule (T-12-03)
 * -- UX-only, the server's FluentValidation rule remains the authoritative
 * gate; a crafted request bypassing the client still gets rejected
 * server-side. Built as a factory so the validation message tracks the
 * active language (react-i18next's `t` from useTranslation('budget')).
 */
function buildBudgetLimitFormSchema(amountPositiveError: string) {
  return z.object({
    limitAmount: z.number({ message: amountPositiveError }).positive(amountPositiveError),
  })
}

type BudgetLimitFormSchema = ReturnType<typeof buildBudgetLimitFormSchema>
type BudgetLimitFormInput = z.input<BudgetLimitFormSchema>
type BudgetLimitFormValues = z.output<BudgetLimitFormSchema>

export interface BudgetLimitDialogCategory {
  id: string
  name: string
  icon: string | null
  color: string | null
}

export interface BudgetLimitDialogProps {
  show: boolean
  onHide: () => void
  category: BudgetLimitDialogCategory | null
  initialLimitAmount?: number
  year: number
  month: number
  onSaved: () => void
}

/**
 * Create/edit a category's (or the global cap's) monthly limit, mirroring
 * CategoryDialog.tsx's Modal + react-hook-form + zod structure.
 * `category: null` means the global cap.
 */
export function BudgetLimitDialog({
  show,
  onHide,
  category,
  initialLimitAmount,
  year,
  month,
  onSaved,
}: BudgetLimitDialogProps) {
  const { t } = useTranslation('budget')
  const { accessToken } = useAuth()
  const { showNotification } = useNotificationContext()
  const upsertMutation = useUpsertBudgetMutation()

  const budgetLimitFormSchema = useMemo(
    () => buildBudgetLimitFormSchema(t('dialog.amountPositiveError')),
    [t]
  )

  const form = useForm<BudgetLimitFormInput, unknown, BudgetLimitFormValues>({
    resolver: zodResolver(budgetLimitFormSchema),
    defaultValues: {
      limitAmount: initialLimitAmount ?? 0,
    },
  })

  useEffect(() => {
    if (!show) return
    form.reset({ limitAmount: initialLimitAmount ?? 0 })
  }, [show, initialLimitAmount, form])

  const isEdit = initialLimitAmount !== undefined

  async function onSubmit(values: BudgetLimitFormValues) {
    try {
      await upsertMutation.mutateAsync({
        accessToken: accessToken ?? '',
        categoryId: category?.id ?? null,
        year,
        month,
        limitAmount: values.limitAmount,
      })
      showNotification({ message: t('saveSuccess'), variant: 'success' })
      onSaved()
      onHide()
    } catch {
      showNotification({ message: t('saveError'), variant: 'danger' })
    }
  }

  return (
    <Modal show={show} onHide={onHide} centered>
      <Modal.Header closeButton>
        <Modal.Title>{isEdit ? t('dialog.editTitle') : t('dialog.setTitle')}</Modal.Title>
      </Modal.Header>

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <Modal.Body>
          <p className="text-muted fs-sm mb-3">{category ? category.name : t('overallCapLabel')}</p>

          <div className="mb-2">
            <FormLabel>{t('dialog.monthlyLimit')}</FormLabel>
            <FormControl
              type="text"
              inputMode="decimal"
              autoComplete="off"
              autoFocus
              isInvalid={!!form.formState.errors.limitAmount}
              {...form.register('limitAmount', { setValueAs: amountValueAs })}
            />
            {form.formState.errors.limitAmount && (
              <div className="text-danger fs-xs mt-1">{form.formState.errors.limitAmount.message}</div>
            )}
          </div>
        </Modal.Body>

        <Modal.Footer>
          <Button variant="light" type="button" onClick={onHide}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={upsertMutation.isPending}>
            {t('dialog.save')}
          </Button>
        </Modal.Footer>
      </Form>
    </Modal>
  )
}
