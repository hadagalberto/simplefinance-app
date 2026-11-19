import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { DashboardPage } from '@/features/dashboard/DashboardPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.dashboard')} />
      <DashboardPage />
    </>
  )
}

export default Page
