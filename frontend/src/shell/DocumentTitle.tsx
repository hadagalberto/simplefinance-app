import { usePageTitle } from './usePageTitle'

type DocumentTitleProps = {
  /** Page name shown before the app name; omit it to show the app name alone. */
  title?: string
}

/**
 * Sets the browser tab title for screens without a page header (auth pages, 404).
 * It delegates to usePageTitle, the single tab-title writer, and renders nothing.
 */
export function DocumentTitle({ title }: DocumentTitleProps) {
  usePageTitle(title)
  return null
}

export default DocumentTitle
