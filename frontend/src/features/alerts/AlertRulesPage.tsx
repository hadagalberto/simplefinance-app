import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Badge,
  Card,
  CardBody,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  Form,
  Modal,
  Placeholder,
  Table,
} from 'react-bootstrap'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'

import { AlertRuleFormDialog } from './AlertRuleFormDialog'
import { NotificationChannelsCard } from './NotificationChannelsCard'
import {
  useAlertRulesQuery,
  useDeleteAlertRuleMutation,
  useToggleAlertRuleActiveMutation,
  type AlertRuleResponse,
} from './api'

function formatCurrency(value: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value)
}

/**
 * Plain-English scope/threshold summary built client-side, e.g. "Category:
 * Groceries", "All accounts", "3 days before due", "Threshold: R$ 500,00".
 * Ported unchanged from v1's AlertRulesPage.tsx.
 */
function buildRuleSummary(rule: AlertRuleResponse, t: (key: string, options?: Record<string, unknown>) => string): string {
  const parts: string[] = []

  if (rule.type === 'BudgetThreshold') {
    parts.push(
      rule.categoryName ? t('summary.categoryLabel', { name: rule.categoryName }) : t('summary.allCategories'),
    )
  }

  if (rule.type === 'LowBalance' || rule.type === 'InvoiceDue' || rule.type === 'LargeTransaction') {
    parts.push(rule.accountName ? t('summary.accountLabel', { name: rule.accountName }) : t('summary.allAccounts'))
  }

  if (rule.threshold != null) {
    parts.push(t('summary.threshold', { value: formatCurrency(rule.threshold) }))
  }

  if (rule.daysBefore != null) {
    parts.push(t('summary.daysBefore', { count: rule.daysBefore }))
  }

  return parts.length > 0 ? parts.join(' · ') : t('summary.empty')
}

function RowSkeleton() {
  return (
    <tr>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 120 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 220 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 70, height: 20 }} />
        </Placeholder>
      </td>
      <td>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 36, height: 20 }} />
        </Placeholder>
      </td>
      <td />
    </tr>
  )
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  const { t } = useTranslation('alerts')

  return (
    <Card>
      <CardBody className="text-center py-5">
        <IconifyIcon icon="lucide:bell-ring" className="fs-1 text-muted mb-3" />
        <h5>{t('empty.title')}</h5>
        <p className="text-muted mb-3">{t('empty.description')}</p>
        <Button variant="primary" onClick={onCreate}>
          {t('empty.cta')}
        </Button>
      </CardBody>
    </Card>
  )
}

/**
 * Settings > Alerts screen (FE-ALRT-01): channel status/test card above an
 * alert rule list -- type, plain-English scope summary, channel badge,
 * active toggle, and a kebab menu (Edit/Delete). Mirrors RulesPage.tsx's
 * page-shell structure exactly.
 */
export function AlertRulesPage() {
  const { t } = useTranslation(['alerts', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const rulesQuery = useAlertRulesQuery(token)
  const toggleActiveMutation = useToggleAlertRuleActiveMutation()
  const deleteMutation = useDeleteAlertRuleMutation()

  const [deleteTarget, setDeleteTarget] = useState<AlertRuleResponse | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editTarget, setEditTarget] = useState<AlertRuleResponse | null>(null)

  const rules = rulesQuery.data ?? []

  function handleAddRule() {
    setEditTarget(null)
    setDialogOpen(true)
  }

  function handleEditRule(rule: AlertRuleResponse) {
    setEditTarget(rule)
    setDialogOpen(true)
  }

  async function handleToggleActive(rule: AlertRuleResponse, checked: boolean) {
    try {
      await toggleActiveMutation.mutateAsync({ accessToken: token, id: rule.id, isActive: checked })
    } catch {
      showNotification({ message: t('alerts:errors.toggle'), variant: 'danger' })
    }
  }

  async function handleDelete() {
    if (!deleteTarget) return
    try {
      await deleteMutation.mutateAsync({ accessToken: token, id: deleteTarget.id })
      showNotification({ message: t('alerts:toasts.deleted'), variant: 'success' })
    } catch {
      showNotification({ message: t('alerts:errors.delete'), variant: 'danger' })
    } finally {
      setDeleteTarget(null)
    }
  }

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
        <h4 className="mb-0">{t('alerts:page.title')}</h4>
        <Button variant="primary" onClick={handleAddRule}>
          <IconifyIcon icon="lucide:plus" className="me-1 align-middle" />
          {t('alerts:page.newRule')}
        </Button>
      </div>

      <NotificationChannelsCard />

      {rulesQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('alerts:errors.load')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void rulesQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!rulesQuery.isPending && !rulesQuery.isError && rules.length === 0 && <EmptyState onCreate={handleAddRule} />}

      {(rulesQuery.isPending || (!rulesQuery.isError && rules.length > 0)) && (
        <Card>
          <div className="table-responsive">
            <Table className="table-custom mb-0">
              <thead>
                <tr>
                  <th>{t('alerts:table.type')}</th>
                  <th>{t('alerts:table.scope')}</th>
                  <th style={{ width: 100 }}>{t('alerts:table.channel')}</th>
                  <th style={{ width: 64 }}>{t('alerts:table.active')}</th>
                  <th style={{ width: 40 }} />
                </tr>
              </thead>
              <tbody>
                {rulesQuery.isPending && (
                  <>
                    <RowSkeleton />
                    <RowSkeleton />
                    <RowSkeleton />
                  </>
                )}
                {!rulesQuery.isPending &&
                  rules.map((rule) => (
                    <tr key={rule.id}>
                      <td>{t(`alerts:types.${rule.type}`)}</td>
                      <td className="text-muted">{buildRuleSummary(rule, t)}</td>
                      <td>
                        <Badge bg="secondary-subtle" text="secondary">
                          {t(`alerts:channels.${rule.channel}`)}
                        </Badge>
                      </td>
                      <td>
                        <Form.Check
                          type="switch"
                          checked={rule.isActive}
                          disabled={toggleActiveMutation.isPending}
                          onChange={(event) => void handleToggleActive(rule, event.target.checked)}
                          aria-label={rule.isActive ? t('alerts:table.deactivate') : t('alerts:table.activate')}
                        />
                      </td>
                      <td>
                        <Dropdown align="end">
                          <DropdownToggle
                            variant="link"
                            className="text-muted drop-arrow-none p-0"
                            aria-label={t('alerts:table.actions')}
                          >
                            <IconifyIcon icon="lucide:more-vertical" />
                          </DropdownToggle>
                          <DropdownMenu>
                            <DropdownItem onClick={() => handleEditRule(rule)}>{t('common:actions.edit')}</DropdownItem>
                            <DropdownItem className="text-danger" onClick={() => setDeleteTarget(rule)}>
                              {t('common:actions.delete')}
                            </DropdownItem>
                          </DropdownMenu>
                        </Dropdown>
                      </td>
                    </tr>
                  ))}
              </tbody>
            </Table>
          </div>
        </Card>
      )}

      <Modal show={!!deleteTarget} onHide={() => setDeleteTarget(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('alerts:deleteModal.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>{t('alerts:deleteModal.body')}</Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setDeleteTarget(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="danger" onClick={() => void handleDelete()} disabled={deleteMutation.isPending}>
            {t('alerts:deleteModal.confirm')}
          </Button>
        </Modal.Footer>
      </Modal>

      <AlertRuleFormDialog
        show={dialogOpen}
        onHide={() => setDialogOpen(false)}
        editTarget={editTarget}
        accessToken={token}
      />
    </div>
  )
}
