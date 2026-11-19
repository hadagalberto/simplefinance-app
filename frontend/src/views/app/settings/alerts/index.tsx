import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { AlertRulesPage } from '@/features/alerts/AlertRulesPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.alerts')} section={t('items.settings')} />
      <AlertRulesPage />
    </>
  )
}

export default Page
