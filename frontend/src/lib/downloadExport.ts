/**
 * Authenticated blob-download helper shared by every export/download surface
 * in the app (Backup here in Plan 13-01; Reports + Transactions in Plan
 * 13-07). apiFetch (apiClient.ts) can't be reused here since its contract
 * always JSON.parses the response body; export/backup responses are file
 * streams (application/json, text/csv, or xlsx's octet-stream-ish
 * content-type), so this reads the same VITE_API_BASE_URL apiClient.ts reads
 * but consumes the response as a Blob and triggers a browser download via a
 * temporary <a> element. Throws on a non-2xx response so callers can catch
 * and show a failure notification rather than silently downloading a broken
 * error body as a fake export file.
 *
 * This module has no React/Bootstrap import and no feature ownership -- it
 * is pure browser fetch/Blob/DOM plumbing any feature can call.
 */
export async function downloadExport(path: string, accessToken: string, filename: string): Promise<void> {
  const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string

  const response = await fetch(`${apiBaseUrl}${path}`, {
    credentials: 'include',
    headers: {
      Authorization: `Bearer ${accessToken}`,
    },
  })

  if (!response.ok) {
    throw new Error('export_failed')
  }

  const blob = await response.blob()
  const objectUrl = URL.createObjectURL(blob)

  const link = document.createElement('a')
  link.href = objectUrl
  link.download = filename
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)

  // T-13-01 (mirrors v1's T-07-18): revoke immediately after the download
  // click so the blob URL is not left reachable in the DOM/memory beyond the
  // triggering action.
  URL.revokeObjectURL(objectUrl)
}
