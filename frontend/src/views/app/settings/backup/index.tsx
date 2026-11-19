import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { BackupPage } from '@/features/backup/BackupPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.backup')} section={t('items.settings')} />
      <BackupPage />
    </>
  )
}

export default Page
