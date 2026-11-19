import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { TransactionsPage } from '@/features/transactions/TransactionsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.transactions')} />
      <TransactionsPage />
    </>
  )
}

export default Page
