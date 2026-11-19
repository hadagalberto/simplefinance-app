import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { BudgetPage } from '@/features/budget/BudgetPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.budget')} />
      <BudgetPage />
    </>
  )
}

export default Page
