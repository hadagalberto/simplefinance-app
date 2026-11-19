import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { ReportsPage } from '@/features/reports/ReportsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.reports')} />
      <ReportsPage />
    </>
  )
}

export default Page
