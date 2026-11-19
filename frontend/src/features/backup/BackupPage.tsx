import { useState } from 'react'
import { Button, Card, CardBody, CardHeader, CardTitle } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { downloadExport } from '@/lib/downloadExport'

const PG_DUMP_COMMAND = 'docker compose exec postgres pg_dump -U simplefinance simplefinance > backup.sql'

/**
 * Settings > Backup screen (FE-BKP-01): a one-click JSON data-dump download
 * reusing the shared downloadExport authenticated-blob-download helper
 * against GET /api/backup/export?format=json (T-13-02 -- same
 * already-reviewed download code path, not a new one), plus documented
 * `pg_dump` instructions for a full infrastructure-level backup, shown as
 * literal copyable text rather than only in a developer-only doc.
 */
export function BackupPage() {
  const { t } = useTranslation('backup')
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const { showNotification } = useNotificationContext()
  const [isDownloading, setIsDownloading] = useState(false)

  async function handleDownload() {
    setIsDownloading(true)
    try {
      const filename = `simplefinance-backup-${new Date().toISOString().slice(0, 10)}.json`
      await downloadExport('/api/backup/export?format=json', token, filename)
    } catch {
      showNotification({ message: t('download.error'), variant: 'danger' })
    } finally {
      setIsDownloading(false)
    }
  }

  return (
    <div style={{ maxWidth: 640 }}>
      <Card className="mb-4">
        <CardHeader>
          <CardTitle as="h4">{t('download.title')}</CardTitle>
          <p className="text-muted mb-0">{t('download.description')}</p>
        </CardHeader>
        <CardBody>
          <Button variant="primary" onClick={() => void handleDownload()} disabled={isDownloading}>
            {isDownloading ? t('download.downloading') : t('download.button')}
          </Button>
        </CardBody>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle as="h4">{t('fullBackup.title')}</CardTitle>
          <p className="text-muted mb-0">{t('fullBackup.description')}</p>
        </CardHeader>
        <CardBody>
          <pre className="p-3 rounded border bg-light-subtle mb-0">
            <code>{PG_DUMP_COMMAND}</code>
          </pre>
        </CardBody>
      </Card>
    </div>
  )
}
