import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { RecurringPage } from '@/features/recurring/RecurringPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.recurring')} />
      <RecurringPage />
    </>
  )
}

export default Page
