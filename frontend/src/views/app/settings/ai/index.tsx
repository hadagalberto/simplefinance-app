import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { AiSettingsPage } from '@/features/ai/AiSettingsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.ai')} section={t('items.settings')} />
      <AiSettingsPage />
    </>
  )
}

export default Page
