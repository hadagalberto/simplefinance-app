import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { GoalsPage } from '@/features/goals/GoalsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.goals')} />
      <GoalsPage />
    </>
  )
}

export default Page
