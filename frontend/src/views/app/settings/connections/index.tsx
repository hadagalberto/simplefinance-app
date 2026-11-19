import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { ConnectionsPage } from '@/features/connections/ConnectionsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.connections')} section={t('items.settings')} />
      <ConnectionsPage />
    </>
  )
}

export default Page
