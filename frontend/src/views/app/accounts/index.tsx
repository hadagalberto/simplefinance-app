import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { AccountsPage } from '@/features/accounts/AccountsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.accounts')} />
      <AccountsPage />
    </>
  )
}

export default Page
