import { useEffect, useMemo, useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import type { TFunction } from 'i18next'
import { Button, Form, FormControl, FormLabel, Modal } from 'react-bootstrap'
import { z } from 'zod'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'

import {
  useCreateCategoryMutation,
  useUpdateCategoryMutation,
  type CategoryResponse,
} from './api'
import { CATEGORY_ICON_NAMES, resolveCategoryIcon } from './icons'

const NO_PARENT_VALUE = 'none'

/**
 * Fixed 10-swatch preset set (mirrors v1's CategoryDialog.tsx exactly),
 * distinct from the reserved finance semantic tokens used for amount-sign
 * styling elsewhere -- those stay reserved for amounts, not arbitrary
 * category decoration.
 */
const COLOR_PRESETS = [
  '#F97316',
  '#EAB308',
  '#84CC16',
  '#14B8A6',
  '#06B6D4',
  '#8B5CF6',
  '#D946EF',
  '#EC4899',
  '#78716C',
  '#0EA5E9',
]

function makeCategoryFormSchema(t: TFunction) {
  return z.object({
    name: z
      .string()
      .trim()
      .min(1, t('dialog.errors.nameRequired'))
      .max(128, t('dialog.errors.nameTooLong')),
    type: z.enum(['Expense', 'Income', 'Transfer']),
    parentCategoryId: z.string(),
    icon: z.string().min(1, t('dialog.errors.iconRequired')),
    color: z.string().min(1, t('dialog.errors.colorRequired')),
  })
}

type CategoryFormValues = z.infer<ReturnType<typeof makeCategoryFormSchema>>

export interface CategoryDialogProps {
  show: boolean
  onHide: () => void
  categories: CategoryResponse[]
  editTarget: CategoryResponse | null
  accessToken: string
}

/**
 * Create/edit form (FE-CAT-01): name, type Select, icon searchable grid
 * picker, color preset swatches, parent Select (top-level categories only
 * -- subcategories cannot themselves have children, keeping the tree
 * exactly 2 levels deep, matching v1). Submits via
 * useCreateCategoryMutation (create) or useUpdateCategoryMutation (edit,
 * pre-filled from editTarget).
 *
 * Icon values render through the app's `@/components/Icon` (bound locally as
 * `IconifyIcon`). `resolveCategoryIcon` already returns the complete
 * `lucide:{slug}` name, and names that already carry a set are passed through
 * unchanged.
 */
export function CategoryDialog({ show, onHide, categories, editTarget, accessToken }: CategoryDialogProps) {
  const { t } = useTranslation(['categories', 'common'])
  const { showNotification } = useNotificationContext()
  const [iconFilter, setIconFilter] = useState('')

  const createMutation = useCreateCategoryMutation()
  const updateMutation = useUpdateCategoryMutation()
  const isPending = createMutation.isPending || updateMutation.isPending

  const categoryFormSchema = useMemo(() => makeCategoryFormSchema(t), [t])

  const form = useForm<CategoryFormValues>({
    resolver: zodResolver(categoryFormSchema),
    defaultValues: {
      name: '',
      type: 'Expense',
      parentCategoryId: NO_PARENT_VALUE,
      icon: 'Tag',
      color: COLOR_PRESETS[0],
    },
  })

  useEffect(() => {
    if (!show) return

    setIconFilter('')

    if (editTarget) {
      form.reset({
        name: editTarget.name,
        type: editTarget.type,
        parentCategoryId: editTarget.parentCategoryId ?? NO_PARENT_VALUE,
        icon: editTarget.icon ?? 'Tag',
        color: editTarget.color ?? COLOR_PRESETS[0],
      })
    } else {
      form.reset({
        name: '',
        type: 'Expense',
        parentCategoryId: NO_PARENT_VALUE,
        icon: 'Tag',
        color: COLOR_PRESETS[0],
      })
    }
  }, [show, editTarget, form])

  // Subcategories can't themselves have children (2-level depth) -- a
  // category can never be reparented under itself.
  const parentOptions = categories.filter(
    (category) => !category.parentCategoryId && category.id !== editTarget?.id
  )

  const filteredIconNames = CATEGORY_ICON_NAMES.filter((name) =>
    name.toLowerCase().includes(iconFilter.trim().toLowerCase())
  )

  async function onSubmit(values: CategoryFormValues) {
    const fields = {
      name: values.name,
      type: values.type,
      icon: values.icon,
      color: values.color,
      parentCategoryId: values.parentCategoryId === NO_PARENT_VALUE ? null : values.parentCategoryId,
    }

    try {
      if (editTarget) {
        await updateMutation.mutateAsync({ accessToken, id: editTarget.id, ...fields })
        showNotification({ message: t('toasts.updated'), variant: 'success' })
      } else {
        await createMutation.mutateAsync({ accessToken, ...fields })
        showNotification({ message: t('toasts.created'), variant: 'success' })
      }
      onHide()
    } catch {
      showNotification({ message: t('dialog.saveError'), variant: 'danger' })
    }
  }

  const iconValue = form.watch('icon')
  const colorValue = form.watch('color')

  return (
    <Modal show={show} onHide={onHide} centered>
      <Modal.Header closeButton>
        <Modal.Title>{editTarget ? t('dialog.titleEdit') : t('dialog.titleAdd')}</Modal.Title>
      </Modal.Header>

      <Form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <Modal.Body>
          <p className="text-muted fs-sm mb-3">
            {editTarget ? t('dialog.descriptionEdit') : t('dialog.descriptionAdd')}
          </p>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.name')}</FormLabel>
            <FormControl
              autoFocus
              isInvalid={!!form.formState.errors.name}
              {...form.register('name')}
            />
            {form.formState.errors.name && (
              <div className="text-danger fs-xs mt-1">{form.formState.errors.name.message}</div>
            )}
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.type')}</FormLabel>
            <Form.Select {...form.register('type')}>
              <option value="Expense">{t('types.Expense')}</option>
              <option value="Income">{t('types.Income')}</option>
              <option value="Transfer">{t('types.Transfer')}</option>
            </Form.Select>
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.parent')}</FormLabel>
            <Form.Select {...form.register('parentCategoryId')}>
              <option value={NO_PARENT_VALUE}>{t('dialog.noParent')}</option>
              {parentOptions.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
            </Form.Select>
          </div>

          <div className="mb-3">
            <FormLabel>{t('dialog.fields.icon')}</FormLabel>
            <FormControl
              placeholder={t('dialog.iconSearchPlaceholder')}
              value={iconFilter}
              onChange={(event) => setIconFilter(event.target.value)}
              className="mb-2"
            />
            <div className="d-flex flex-wrap g-2" style={{ gap: '0.5rem', maxHeight: 180, overflowY: 'auto' }}>
              {filteredIconNames.length === 0 && <span className="text-muted fs-sm">{t('dialog.noIconFound')}</span>}
              {filteredIconNames.map((name) => (
                <button
                  key={name}
                  type="button"
                  className={`btn btn-sm d-flex align-items-center gap-1 ${
                    iconValue === name ? 'btn-primary' : 'btn-outline-secondary'
                  }`}
                  onClick={() => form.setValue('icon', name, { shouldValidate: true })}
                >
                  <IconifyIcon icon={resolveCategoryIcon(name)} />
                  {name}
                </button>
              ))}
            </div>
            {form.formState.errors.icon && (
              <div className="text-danger fs-xs mt-1">{form.formState.errors.icon.message}</div>
            )}
          </div>

          <div className="mb-2">
            <FormLabel>{t('dialog.fields.color')}</FormLabel>
            <div className="d-flex flex-wrap" style={{ gap: '0.5rem' }}>
              {COLOR_PRESETS.map((color) => (
                <button
                  key={color}
                  type="button"
                  className="d-flex align-items-center justify-content-center rounded-circle border-0"
                  style={{
                    backgroundColor: color,
                    width: 32,
                    height: 32,
                    outline: colorValue === color ? '2px solid var(--bs-body-color)' : 'none',
                    outlineOffset: 2,
                  }}
                  onClick={() => form.setValue('color', color, { shouldValidate: true })}
                  aria-label={t('dialog.selectColor', { color })}
                >
                  {colorValue === color && <IconifyIcon icon="lucide:check" className="text-white" />}
                </button>
              ))}
            </div>
            {form.formState.errors.color && (
              <div className="text-danger fs-xs mt-1">{form.formState.errors.color.message}</div>
            )}
          </div>
        </Modal.Body>

        <Modal.Footer>
          <Button variant="light" type="button" onClick={onHide}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={isPending}>
            {editTarget ? t('dialog.saveChanges') : t('dialog.createCategory')}
          </Button>
        </Modal.Footer>
      </Form>
    </Modal>
  )
}
