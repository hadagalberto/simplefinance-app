import { useEffect } from 'react'

import { formatPageTitle } from './pageTitle'

/**
 * Writes the browser tab title for the current screen ("<title> · SimpleFinance").
 * This hook is the only code that sets document.title. On unmount it puts back the
 * title that was there before, so moving between lazy screens never leaves a stale one.
 */
export function usePageTitle(title?: string): void {
  const formatted = formatPageTitle(title)

  useEffect(() => {
    const previous = document.title
    document.title = formatted
    return () => {
      document.title = previous
    }
  }, [formatted])
}

export default usePageTitle
