import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { RulesPage } from '@/features/rules/RulesPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.rules')} section={t('items.settings')} />
      <RulesPage />
    </>
  )
}

export default Page
