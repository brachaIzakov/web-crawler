interface Props {
  page: number
  pageSize: number
  totalCount: number
  onChange: (page: number) => void
}

export function Pagination({ page, pageSize, totalCount, onChange }: Props) {
  const pages = Math.max(1, Math.ceil(totalCount / pageSize))
  return (
    <nav className="pagination" aria-label="Pagination">
      <button type="button" disabled={page <= 1} onClick={() => onChange(page - 1)}>‹ Prev</button>
      <span>Page {page} of {pages} · {totalCount} job{totalCount === 1 ? '' : 's'}</span>
      <button type="button" disabled={page >= pages} onClick={() => onChange(page + 1)}>Next ›</button>
    </nav>
  )
}
