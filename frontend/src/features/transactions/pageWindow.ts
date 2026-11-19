// The list of page buttons the transactions pager shows. This module has no
// imports, so the node tests in scripts/ui-logic.test.mjs can load it directly.

/** A 0-based page index, or a gap that stands for two or more hidden pages. */
export type PageSlot = number | 'gap'

const SHOW_ALL_UP_TO = 7

/**
 * Returns the page slots for `pageCount` pages with `current` selected (both
 * 0-based). Up to 7 pages are all shown. Beyond that the first page, the last
 * page and the pages next to the (clamped) current one are shown; a single
 * hidden page between two shown ones is shown as well, and a longer run of
 * hidden pages becomes one 'gap'. The result never holds more than 7 slots.
 */
export function pageWindow(pageCount: number, current: number): PageSlot[] {
  const count = Math.max(0, Math.floor(pageCount))
  if (count <= SHOW_ALL_UP_TO) {
    return Array.from({ length: count }, (_, index) => index)
  }

  const last = count - 1
  const selected = Math.min(Math.max(Math.floor(current), 0), last)
  const shown = [...new Set([0, selected - 1, selected, selected + 1, last])]
    .filter((index) => index >= 0 && index <= last)
    .sort((a, b) => a - b)

  const slots: PageSlot[] = []
  let previous = -1
  for (const index of shown) {
    if (previous >= 0) {
      const jump = index - previous
      if (jump === 2) slots.push(previous + 1)
      else if (jump > 2) slots.push('gap')
    }
    slots.push(index)
    previous = index
  }
  return slots
}
