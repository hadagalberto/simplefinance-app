import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { AssistantPage } from '@/features/ai/AssistantPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.assistant')} />
      <AssistantPage />
    </>
  )
}

export default Page
