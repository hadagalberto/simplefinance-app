import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { NetWorthPage } from '@/features/networth/NetWorthPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.netWorth')} />
      <NetWorthPage />
    </>
  )
}

export default Page
