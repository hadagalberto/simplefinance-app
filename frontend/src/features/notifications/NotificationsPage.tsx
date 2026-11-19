import { useTranslation } from 'react-i18next'
import { Alert, Badge, Button, Card, CardBody, Placeholder } from 'react-bootstrap'

import { useAuth } from '@/features/auth/useAuth'

import { useMarkNotificationReadMutation, useNotificationsQuery, type NotificationItem } from './api'

function formatNotificationDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit' }).format(new Date(dateString))
}

function RowSkeleton() {
  return (
    <Card>
      <CardBody className="d-flex flex-column gap-2">
        <Placeholder as="span" animation="glow">
          <Placeholder xs={4} />
        </Placeholder>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} />
        </Placeholder>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={2} />
        </Placeholder>
      </CardBody>
    </Card>
  )
}

function EmptyState() {
  const { t } = useTranslation('notifications')

  return (
    <Card>
      <CardBody className="text-center py-5">
        <h5>{t('page.empty.title')}</h5>
        <p className="text-muted mb-0">{t('page.empty.description')}</p>
      </CardBody>
    </Card>
  )
}

function NotificationRow({
  item,
  onSelect,
  t,
}: {
  item: NotificationItem
  onSelect: (item: NotificationItem) => void
  t: (key: string, options?: Record<string, unknown>) => string
}) {
  return (
    <Card
      role="button"
      tabIndex={0}
      onClick={() => onSelect(item)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onSelect(item)
        }
      }}
      className={!item.isRead ? 'border-start border-4 border-primary' : undefined}
    >
      <CardBody className="d-flex flex-column gap-1">
        <div className="d-flex align-items-center justify-content-between gap-3">
          <span className={item.isRead ? 'fw-normal' : 'fw-semibold'}>{item.title}</span>
          <Badge bg="secondary-subtle" text="secondary" className="flex-shrink-0">
            {t(`channels.${item.channel}`)}
          </Badge>
        </div>
        <p className="text-muted mb-0">{item.message}</p>
        <span className="fs-xs text-muted">{formatNotificationDate(item.createdAt)}</span>
      </CardBody>
    </Card>
  )
}

/**
 * Full notification history (NOTIF-04) -- everything beyond the topbar
 * bell's 5-item dropdown, reusing the same useNotificationsQuery hook (no
 * separate paginated endpoint exists yet; GET /api/notifications already
 * returns newest-first up to its server-side take cap). Clicking a row marks
 * it read, same interaction as the dropdown. "Mark all as read" is new
 * relative to v1 (fires N sequential POST /{id}/read calls -- no bulk
 * endpoint exists, see 13-05-PLAN.md's threat register T-13-10).
 */
export function NotificationsPage() {
  const { t } = useTranslation(['notifications', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const notificationsQuery = useNotificationsQuery(token)
  const markReadMutation = useMarkNotificationReadMutation()

  const items = notificationsQuery.data?.items ?? []
  const unreadItems = items.filter((item) => !item.isRead)

  function handleSelect(item: NotificationItem) {
    if (item.isRead) return
    markReadMutation.mutate({ accessToken: token, id: item.id })
  }

  function handleMarkAllRead() {
    for (const item of unreadItems) {
      markReadMutation.mutate({ accessToken: token, id: item.id })
    }
  }

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
        <h4 className="mb-0">{t('page.title')}</h4>
        <Button
          variant="outline-secondary"
          size="sm"
          onClick={handleMarkAllRead}
          disabled={unreadItems.length === 0 || markReadMutation.isPending}
        >
          {t('page.markAllRead')}
        </Button>
      </div>

      {notificationsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('page.errors.load')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void notificationsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {!notificationsQuery.isPending && !notificationsQuery.isError && items.length === 0 && <EmptyState />}

      {notificationsQuery.isPending && (
        <div className="d-flex flex-column gap-3">
          <RowSkeleton />
          <RowSkeleton />
          <RowSkeleton />
        </div>
      )}

      {!notificationsQuery.isPending && !notificationsQuery.isError && items.length > 0 && (
        <div className="d-flex flex-column gap-3">
          {items.map((item) => (
            <NotificationRow key={item.id} item={item} onSelect={handleSelect} t={t} />
          ))}
        </div>
      )}
    </div>
  )
}
