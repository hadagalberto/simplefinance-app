import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { InvestmentsPage } from '@/features/investments/InvestmentsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.investments')} />
      <InvestmentsPage />
    </>
  )
}

export default Page
