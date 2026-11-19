import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { NotificationsPage } from '@/features/notifications/NotificationsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.notifications')} />
      <NotificationsPage />
    </>
  )
}

export default Page
