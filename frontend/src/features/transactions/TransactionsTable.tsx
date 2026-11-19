import { flexRender, type Header, type Row, type Table } from '@tanstack/react-table'
import type { KeyboardEvent } from 'react'

import Icon from '@/components/Icon'

type TransactionsTableProps<TData> = {
  table: Table<TData>
  emptyMessage: string
  onRowClick?: (item: TData) => void
}

function ariaSortFor<TData>(header: Header<TData, unknown>): 'ascending' | 'descending' | 'none' | undefined {
  if (!header.column.getCanSort()) return undefined
  const direction = header.column.getIsSorted()
  if (direction === 'asc') return 'ascending'
  if (direction === 'desc') return 'descending'
  return 'none'
}

function HeaderContent<TData>({ header }: { header: Header<TData, unknown> }) {
  if (header.isPlaceholder) return null
  const content = flexRender(header.column.columnDef.header, header.getContext())
  if (!header.column.getCanSort()) return <>{content}</>

  const direction = header.column.getIsSorted()
  return (
    <button type="button" className="sf-sort-btn" onClick={header.column.getToggleSortingHandler()}>
      {content}
      {direction === 'asc' && <Icon icon="arrow-up" />}
      {direction === 'desc' && <Icon icon="arrow-down" />}
    </button>
  )
}

/**
 * Renders a TanStack table instance as the transactions list. With
 * `onRowClick`, a row opens by click or by Enter/Space while the row itself
 * has focus; keys pressed inside a cell's own controls are left to them.
 */
export function TransactionsTable<TData>({ table, emptyMessage, onRowClick }: TransactionsTableProps<TData>) {
  const rows = table.getRowModel().rows
  const columnCount = Math.max(1, table.getVisibleLeafColumns().length)

  function handleRowKeyDown(row: Row<TData>, event: KeyboardEvent<HTMLTableRowElement>) {
    if (!onRowClick || event.target !== event.currentTarget) return
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onRowClick(row.original)
    }
  }

  return (
    <div className="table-responsive">
      <table className="table table-custom table-hover align-middle mb-0">
        <thead className="thead-sm">
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id} className="text-uppercase fs-xxs">
              {headerGroup.headers.map((header) => (
                <th key={header.id} colSpan={header.colSpan} aria-sort={ariaSortFor(header)}>
                  <HeaderContent header={header} />
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columnCount} className="text-center text-muted py-4">
                {emptyMessage}
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr
                key={row.id}
                className={onRowClick ? 'sf-row-clickable' : undefined}
                tabIndex={onRowClick ? 0 : undefined}
                onClick={onRowClick ? () => onRowClick(row.original) : undefined}
                onKeyDown={onRowClick ? (event) => handleRowKeyDown(row, event) : undefined}
              >
                {row.getVisibleCells().map((cell) => (
                  <td key={cell.id}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  )
}

export default TransactionsTable
