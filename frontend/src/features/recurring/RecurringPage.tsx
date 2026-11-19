import { Icon as IconifyIcon } from '@/components/Icon'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  OverlayTrigger,
  Placeholder,
  Table,
  Tooltip,
} from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { MoneyText } from '@/components/MoneyText'
import { StatTile } from '@/components/StatTile'
import { resolveCategoryIcon } from '@/features/categories/icons'
import { useAuth } from '@/features/auth/useAuth'
import { useNotificationContext } from '@/shell/toast/useNotificationContext'

import {
  useRecurringQuery,
  useToggleActiveMutation,
  useToggleSubscriptionMutation,
  type RecurringItem,
} from './api'

const dateFormatter = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' })

function formatRowDate(dateString: string): string {
  return dateFormatter.format(new Date(dateString))
}

function StatTileSkeleton() {
  return (
    <Card style={{ maxWidth: 320 }}>
      <CardBody>
        <Placeholder as="p" animation="glow" className="mb-1">
          <Placeholder xs={7} />
        </Placeholder>
        <Placeholder as="p" animation="glow" className="mb-0">
          <Placeholder xs={4} style={{ height: 28 }} />
        </Placeholder>
      </CardBody>
    </Card>
  )
}

function RowSkeleton() {
  return (
    <tr>
      {Array.from({ length: 8 }).map((_, index) => (
        <td key={index}>
          <Placeholder as="span" animation="glow">
            <Placeholder xs={8} />
          </Placeholder>
        </td>
      ))}
    </tr>
  )
}

interface RecurringRowProps {
  item: RecurringItem
  accessToken: string
}

/**
 * One Recurring table row: name (+ "New"/"Price increase" badges per
 * RECUR-04), category, avg amount, frequency, last/next charge, yearly
 * total, and a kebab menu toggling subscription/active state -- each action
 * label reflects the OPPOSITE of the row's current state, mirroring
 * TransactionsPage's kebab-menu "Hide"/"Show" convention. Cancelled
 * (isActive=false) rows render muted (opacity 0.5) without being removed
 * from the list.
 */
function RecurringRow({ item, accessToken }: RecurringRowProps) {
  const { t } = useTranslation('recurring')
  const { showNotification } = useNotificationContext()
  const toggleSubscriptionMutation = useToggleSubscriptionMutation()
  const toggleActiveMutation = useToggleActiveMutation()

  async function handleToggleSubscription() {
    try {
      await toggleSubscriptionMutation.mutateAsync({
        accessToken,
        id: item.id,
        isSubscription: !item.isSubscription,
      })
    } catch {
      showNotification({ message: t('updateError'), variant: 'danger' })
    }
  }

  async function handleToggleActive() {
    try {
      await toggleActiveMutation.mutateAsync({
        accessToken,
        id: item.id,
        isActive: !item.isActive,
      })
    } catch {
      showNotification({ message: t('updateError'), variant: 'danger' })
    }
  }

  const categoryIcon = item.categoryId ? resolveCategoryIcon(item.categoryIcon) : 'lucide:wallet'

  return (
    <tr style={!item.isActive ? { opacity: 0.5 } : undefined}>
      <td className="fw-semibold">
        <div className="d-flex align-items-center gap-2">
          <span>{item.displayName}</span>
          {item.isNewlyDetected && <Badge bg="secondary">{t('row.newBadge')}</Badge>}
          {item.amountIncreaseDetected && (
            <OverlayTrigger
              placement="top"
              overlay={
                <Tooltip>
                  {item.previousAverageAmount !== null && (
                    <span className="d-inline-flex align-items-center gap-1">
                      <MoneyText amount={item.previousAverageAmount} variant="neutral" showSign={false} />
                      {' -> '}
                      <MoneyText amount={item.averageAmount} variant="neutral" showSign={false} />
                    </span>
                  )}
                </Tooltip>
              }
            >
              <Badge bg="danger-subtle" text="danger" className="d-inline-flex align-items-center gap-1">
                <IconifyIcon icon="lucide:trending-up" />
                {t('row.priceIncreaseBadge')}
              </Badge>
            </OverlayTrigger>
          )}
        </div>
      </td>
      <td>
        <div className="d-flex align-items-center gap-2">
          <IconifyIcon icon={categoryIcon} className="text-muted" />
          {item.categoryId && item.categoryColor && (
            <span
              className="d-inline-block rounded-circle"
              style={{ width: 8, height: 8, backgroundColor: item.categoryColor }}
            />
          )}
          <span>{item.categoryId ? item.categoryName : t('row.uncategorized')}</span>
        </div>
      </td>
      <td className="text-end">
        <MoneyText amount={item.averageAmount} variant="expense" />
      </td>
      <td>
        <Badge bg="secondary">{item.frequency}</Badge>
      </td>
      <td className="text-muted">{formatRowDate(item.lastSeenDate)}</td>
      <td className="text-muted">{formatRowDate(item.nextExpectedDate)}</td>
      <td className="text-end">
        <MoneyText amount={item.yearlyTotal} variant="expense" />
      </td>
      <td>
        <Dropdown align="end">
          <DropdownToggle
            as="div"
            className="text-muted drop-arrow-none card-drop p-0 cursor-pointer"
            aria-label={t('row.actionsAriaLabel')}
          >
            <IconifyIcon icon="lucide:ellipsis-vertical" className="fs-lg" />
          </DropdownToggle>
          <DropdownMenu>
            <DropdownItem onClick={() => void handleToggleSubscription()}>
              {item.isSubscription ? t('row.unmarkAsSubscription') : t('row.markAsSubscription')}
            </DropdownItem>
            <DropdownItem onClick={() => void handleToggleActive()}>
              {item.isActive ? t('row.markCancelled') : t('row.markActive')}
            </DropdownItem>
          </DropdownMenu>
        </Dropdown>
      </td>
    </tr>
  )
}

/**
 * FE-REC-01: the full Recurring (Subscriptions) screen -- monthly committed
 * cost stat tile, the table of detected recurring charges with new/price-
 * increase badges, and per-row subscription/active toggles, wired to
 * v1.0 Phase 7's unchanged GET/PATCH /api/recurring.
 */
export function RecurringPage() {
  const { t } = useTranslation('recurring')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const recurringQuery = useRecurringQuery(token)

  const data = recurringQuery.data
  const items = data?.items ?? []
  const isInitialLoading = recurringQuery.isLoading
  const isEmpty = !isInitialLoading && !recurringQuery.isError && items.length === 0

  return (
    <div>
      <h4 className="mb-3">{t('pageTitle')}</h4>

      {recurringQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void recurringQuery.refetch()}>
            {t('retry')}
          </Button>
        </Alert>
      )}

      {!recurringQuery.isError && (
        <div className="mb-3" style={{ maxWidth: 320 }}>
          {isInitialLoading ? (
            <StatTileSkeleton />
          ) : (
            <StatTile
              label={t('statTile.monthlyCommittedCost')}
              amount={data?.monthlyCommittedCost ?? 0}
              variant="expense"
            />
          )}
        </div>
      )}

      {!recurringQuery.isError && isEmpty && (
        <Card>
          <CardBody className="text-center py-5">
            <h5>{t('empty.title')}</h5>
            <p className="text-muted mb-0">{t('empty.description')}</p>
          </CardBody>
        </Card>
      )}

      {!recurringQuery.isError && !isEmpty && (
        <Card>
          <div className="table-responsive">
            <Table className="table-custom mb-0">
              <thead>
                <tr>
                  <th>{t('table.name')}</th>
                  <th>{t('table.category')}</th>
                  <th className="text-end">{t('table.avgAmount')}</th>
                  <th>{t('table.frequency')}</th>
                  <th>{t('table.lastCharge')}</th>
                  <th>{t('table.nextEstimated')}</th>
                  <th className="text-end">{t('table.yearlyTotal')}</th>
                  <th style={{ width: 40 }} />
                </tr>
              </thead>
              <tbody>
                {isInitialLoading && Array.from({ length: 6 }).map((_, index) => <RowSkeleton key={index} />)}

                {!isInitialLoading &&
                  items.map((item) => <RecurringRow key={item.id} item={item} accessToken={token} />)}
              </tbody>
            </Table>
          </div>
        </Card>
      )}
    </div>
  )
}
