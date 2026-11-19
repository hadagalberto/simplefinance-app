import { useTranslation } from 'react-i18next'

import Icon from '@/components/Icon'
import { pageWindow } from '@/features/transactions/pageWindow'

type TransactionsPagerProps = {
  totalItems: number
  start: number
  end: number
  /** 0-based index of the current page. */
  pageIndex: number
  pageCount: number
  /** Receives the 0-based index of the page to show. */
  onPageChange: (index: number) => void
}

/**
 * Footer of the transactions list: a "showing X-Y of N" summary and a page
 * list that keeps at most seven page buttons (first, last, the neighbours of
 * the current page and ellipses), so it wraps instead of overflowing a phone.
 */
export function TransactionsPager({ totalItems, start, end, pageIndex, pageCount, onPageChange }: TransactionsPagerProps) {
  const { t } = useTranslation('transactions')
  const canGoBack = pageIndex > 0
  const canGoForward = pageIndex < pageCount - 1

  return (
    <div className="sf-pager">
      <span className="text-muted">
        {t('pagination.summary', { start, end, total: totalItems, items: t('pagination.itemsName') })}
      </span>
      <nav aria-label={t('pagination.label')}>
        <ul className="pagination pagination-sm flex-wrap mb-0">
          <li className={canGoBack ? 'page-item' : 'page-item disabled'}>
            <button
              type="button"
              className="page-link"
              aria-label={t('pagination.previous')}
              disabled={!canGoBack}
              onClick={() => onPageChange(pageIndex - 1)}
            >
              <Icon icon="chevron-left" />
            </button>
          </li>
          {pageWindow(pageCount, pageIndex).map((slot, position) =>
            slot === 'gap' ? (
              <li key={`gap-${position}`} className="page-item disabled" aria-hidden="true">
                <span className="page-link">…</span>
              </li>
            ) : (
              <li key={slot} className={slot === pageIndex ? 'page-item active' : 'page-item'}>
                <button
                  type="button"
                  className="page-link"
                  aria-current={slot === pageIndex ? 'page' : undefined}
                  onClick={() => onPageChange(slot)}
                >
                  {slot + 1}
                </button>
              </li>
            ),
          )}
          <li className={canGoForward ? 'page-item' : 'page-item disabled'}>
            <button
              type="button"
              className="page-link"
              aria-label={t('pagination.next')}
              disabled={!canGoForward}
              onClick={() => onPageChange(pageIndex + 1)}
            >
              <Icon icon="chevron-right" />
            </button>
          </li>
        </ul>
      </nav>
    </div>
  )
}

export default TransactionsPager
