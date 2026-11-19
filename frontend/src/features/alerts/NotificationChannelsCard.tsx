import { useTranslation } from 'react-i18next'
import { Alert, Badge, Button, Card, CardBody, CardHeader, OverlayTrigger, Placeholder, Tooltip } from 'react-bootstrap'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'

import { useNotificationChannelsQuery, useTestNotificationChannelMutation, type NotificationChannel } from './api'

function RowSkeleton() {
  return (
    <div className="d-flex align-items-center justify-content-between gap-3 py-2">
      <Placeholder as="span" animation="glow">
        <Placeholder xs={12} style={{ width: 90 }} />
      </Placeholder>
      <div className="d-flex align-items-center gap-2">
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 100, height: 20 }} />
        </Placeholder>
        <Placeholder as="span" animation="glow">
          <Placeholder xs={12} style={{ width: 90, height: 30 }} />
        </Placeholder>
      </div>
    </div>
  )
}

/**
 * Settings > Alerts channel-status card (FE-ALRT-01): one row per channel
 * (Telegram/Email) showing whether it's configured plus a "Send test"
 * button. The button is disabled (not hidden) when unconfigured, with a
 * tooltip explaining why -- matches the app's established
 * disabled-with-explanation convention.
 */
export function NotificationChannelsCard() {
  const { t } = useTranslation(['alerts', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()

  const channelsQuery = useNotificationChannelsQuery(token)
  const testMutation = useTestNotificationChannelMutation()

  const channels = channelsQuery.data ?? []

  async function handleSendTest(type: NotificationChannel) {
    try {
      const result = await testMutation.mutateAsync({ accessToken: token, type })
      if (result.sent) {
        showNotification({ message: t('alerts:channelsCard.toasts.testSent'), variant: 'success' })
      } else {
        showNotification({ message: t('alerts:channelsCard.errors.test'), variant: 'danger' })
      }
    } catch {
      showNotification({ message: t('alerts:channelsCard.errors.test'), variant: 'danger' })
    }
  }

  return (
    <Card className="mb-4">
      <CardHeader>
        <h5 className="mb-0">{t('alerts:channelsCard.title')}</h5>
        <p className="text-muted fs-sm mb-0">{t('alerts:channelsCard.description')}</p>
      </CardHeader>
      <CardBody>
        {channelsQuery.isError && (
          <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3 mb-0">
            <span>{t('alerts:channelsCard.errors.load')}</span>
            <Button variant="outline-danger" size="sm" onClick={() => void channelsQuery.refetch()}>
              {t('common:actions.retry')}
            </Button>
          </Alert>
        )}

        {channelsQuery.isPending && (
          <div className="d-flex flex-column gap-1">
            <RowSkeleton />
            <RowSkeleton />
          </div>
        )}

        {!channelsQuery.isPending && !channelsQuery.isError && (
          <div className="d-flex flex-column">
            {channels.map((channel, index) => {
              const isTestingThis = testMutation.isPending && testMutation.variables?.type === channel.type
              const label = t(`alerts:channels.${channel.type as Exclude<NotificationChannel, 'Both'>}`)

              const testButton = (
                <Button
                  variant="outline-secondary"
                  size="sm"
                  disabled={!channel.isConfigured || testMutation.isPending}
                  onClick={() => void handleSendTest(channel.type)}
                >
                  {isTestingThis ? t('alerts:channelsCard.sending') : t('alerts:channelsCard.sendTest')}
                </Button>
              )

              return (
                <div
                  key={channel.type}
                  className={`d-flex align-items-center justify-content-between gap-3 py-2 ${index > 0 ? 'border-top' : ''}`}
                >
                  <span className="fw-medium">{label}</span>
                  <div className="d-flex align-items-center gap-2">
                    <Badge
                      bg={channel.isConfigured ? 'success-subtle' : 'secondary-subtle'}
                      text={channel.isConfigured ? 'success' : 'secondary'}
                    >
                      {channel.isConfigured ? t('alerts:channelsCard.configured') : t('alerts:channelsCard.notConfigured')}
                    </Badge>
                    {channel.isConfigured ? (
                      testButton
                    ) : (
                      <OverlayTrigger
                        placement="top"
                        overlay={<Tooltip>{t('alerts:channelsCard.setupTooltip', { channel: label })}</Tooltip>}
                      >
                        <span>{testButton}</span>
                      </OverlayTrigger>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </CardBody>
    </Card>
  )
}
