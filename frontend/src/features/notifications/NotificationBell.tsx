import { useTranslation } from 'react-i18next'
import { Badge, Col, Dropdown, DropdownItem, DropdownMenu, DropdownToggle, Row } from 'react-bootstrap'
import { useNavigate } from 'react-router'

import Icon from '@/components/Icon'
import { useAuth } from '@/features/auth/useAuth'

import { useMarkNotificationReadMutation, useNotificationsQuery, type NotificationItem } from './api'

/** Only the 5 most recent items fit the dropdown; the rest live on /notifications. */
const DROPDOWN_ITEM_LIMIT = 5

function formatNotificationDate(dateString: string): string {
  return new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: '2-digit' }).format(new Date(dateString))
}

/** Caps the DISPLAYED badge count at "9+"; the underlying unreadCount value itself is unrounded. */
function formatBadgeCount(unreadCount: number): string {
  return unreadCount > 9 ? '9+' : String(unreadCount)
}

/**
 * Topbar bell backed by GET /api/notifications and POST /{id}/read.
 * Shows an unread badge, a dropdown of the 5 most recent notifications with
 * mark-read on click, and a "View all" link to the full-history page.
 */
export function NotificationBell() {
  const { t } = useTranslation('notifications')
  const navigate = useNavigate()
  const { accessToken } = useAuth()
  const token = accessToken ?? ''

  const notificationsQuery = useNotificationsQuery(token)
  const markReadMutation = useMarkNotificationReadMutation()

  const unreadCount = notificationsQuery.data?.unreadCount ?? 0
  const items = notificationsQuery.data?.items.slice(0, DROPDOWN_ITEM_LIMIT) ?? []

  function handleItemSelect(item: NotificationItem) {
    if (item.isRead) return
    markReadMutation.mutate({ accessToken: token, id: item.id })
  }

  return (
    <div className="sf-topbar__item">
      <Dropdown align="end">
        <DropdownToggle className="sf-topbar__btn" as="button">
          <span className="sf-topbar__icon">
            <Icon icon="bell" />
          </span>
          {unreadCount > 0 && (
            <span className="badge text-bg-danger badge-circle sf-topbar__badge">{formatBadgeCount(unreadCount)}</span>
          )}
        </DropdownToggle>

        <DropdownMenu className="p-0 dropdown-menu-end dropdown-menu-lg">
          <div className="px-3 py-2 border-bottom">
            <Row className="align-items-center">
              <Col>
                <h6 className="m-0 fs-md fw-semibold">{t('bell.title')}</h6>
              </Col>
            </Row>
          </div>

          {items.length === 0 ? (
            <p className="px-3 py-4 mb-0 text-center text-muted">{t('bell.empty')}</p>
          ) : (
            <div className="sf-notification__list">
              {items.map((item) => (
                <DropdownItem
                  key={item.id}
                  className="sf-notification py-2 text-wrap"
                  onClick={() => handleItemSelect(item)}
                >
                  <div className="d-flex align-items-center justify-content-between gap-2">
                    <span className={item.isRead ? 'fw-normal' : 'fw-semibold'}>{item.title}</span>
                    <Badge bg="secondary-subtle" text="secondary" className="flex-shrink-0">
                      {t(`channels.${item.channel}`)}
                    </Badge>
                  </div>
                  <p className="text-muted text-truncate mb-0">{item.message}</p>
                  <span className="fs-xs text-muted">{formatNotificationDate(item.createdAt)}</span>
                </DropdownItem>
              ))}
            </div>
          )}

          <DropdownItem
            className="text-center text-reset text-decoration-underline link-offset-2 fw-bold sf-notification__footer border-top border-light py-2"
            onClick={() => navigate('/notifications')}
          >
            {t('bell.viewAll')}
          </DropdownItem>
        </DropdownMenu>
      </Dropdown>
    </div>
  )
}
