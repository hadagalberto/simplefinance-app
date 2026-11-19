import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { PreferencesPage } from '@/features/preferences/PreferencesPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.preferences')} section={t('items.settings')} />
      <PreferencesPage />
    </>
  )
}

export default Page
