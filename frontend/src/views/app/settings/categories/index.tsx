import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { CategoriesPage } from '@/features/categories/CategoriesPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.categories')} section={t('items.settings')} />
      <CategoriesPage />
    </>
  )
}

export default Page
