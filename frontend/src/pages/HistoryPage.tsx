import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import { isTerminal, type JobListResponse, type JobStatus } from '../api/types'
import { durationBetween, fmtDuration } from '../format'
import { useNow } from '../hooks/useNow'
import { usePolling } from '../hooks/usePolling'
import { useRerun } from '../hooks/useRerun'
import { ErrorMessage } from '../components/ErrorMessage'
import { Loading } from '../components/Loading'
import { Pagination } from '../components/Pagination'
import { StatusBadge } from '../components/StatusBadge'
import { Time } from '../components/Time'

const PAGE_SIZES = [10, 20, 50] as const
const REFRESH_MS = 5000

type Filter = 'All' | 'Running' | 'Completed' | 'Failed' | 'Canceled'
const FILTERS: readonly Filter[] = ['All', 'Running', 'Completed', 'Failed', 'Canceled']

/** "Running" also covers queued (Pending) jobs. */
const matches = (f: Filter, s: JobStatus): boolean =>
  f === 'All' || s === f || (f === 'Running' && s === 'Pending')

const hasActive = (d: JobListResponse | null): boolean => d?.items.some((i) => !isTerminal(i.status)) ?? false

export function HistoryPage() {
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState<number>(20)
  const [filter, setFilter] = useState<Filter>('All')
  const now = useNow(10_000)
  const rerun = useRerun()
  const { data, error, loading, reload } = usePolling(
    (s) => api.listJobs(page, pageSize, s),
    `${page}:${pageSize}`,
    (d) => (hasActive(d) ? REFRESH_MS : null),
  )

  const autoRefreshing = hasActive(data)
  const visible = data ? data.items.filter((j) => matches(filter, j.status)) : []

  return (
    <div className="card">
      <div className="title-row">
        <h1>History</h1>
        {autoRefreshing && <span className="muted hint" title="Refreshing every 5 s while jobs are running">● auto-refreshing</span>}
        <span className="spacer" />
        <button type="button" onClick={reload} disabled={loading}>Refresh</button>
      </div>

      <div className="history-toolbar">
        <div className="chips" role="group" aria-label="Filter by status (this page)">
          {FILTERS.map((f) => (
            <button key={f} type="button" className="chip" aria-pressed={filter === f} onClick={() => setFilter(f)}
              title={f === 'Running' ? 'Running and queued jobs' : undefined}>
              {f}
            </button>
          ))}
        </div>
        <span className="spacer" />
        <label className="inline-label">
          Per page
          <select value={pageSize} onChange={(e) => { setPageSize(Number(e.target.value)); setPage(1) }}>
            {PAGE_SIZES.map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
      </div>

      {rerun.error && <ErrorMessage title="Re-run failed" error={rerun.error} />}
      {error && <ErrorMessage title="Couldn't load history" error={error} onRetry={reload} />}
      {loading && !data && <Loading label="Loading history…" lines={4} />}

      {data && data.totalCount === 0 && (
        <div className="empty">
          <p>No crawl jobs yet.</p>
          <Link className="button-link primary" to="/">Start your first crawl</Link>
        </div>
      )}
      {data && data.totalCount > 0 && visible.length === 0 && (
        <div className="empty">
          <p>No {filter.toLowerCase()} jobs on this page.</p>
          <button type="button" onClick={() => setFilter('All')}>Show all</button>
        </div>
      )}

      {visible.length > 0 && (
        <table className="history-table">
          <thead>
            <tr>
              <th>Created</th><th>URL</th><th>Status</th><th>Crawled / discovered</th>
              <th>Depth</th><th>Duration</th><th><span className="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((j) => {
              const running = !isTerminal(j.status)
              const dur = durationBetween(j.startedAt, j.completedAt, now)
              return (
                <tr key={j.jobId}>
                  <td data-label="Created"><Time iso={j.createdAt} now={now} /></td>
                  <td data-label="URL" className="url-cell">
                    <Link className="mono" to={`/jobs/${j.jobId}`} title={j.inputUrl}>{j.inputUrl}</Link>
                  </td>
                  <td data-label="Status"><StatusBadge status={j.status} /></td>
                  <td data-label="Crawled / discovered">{j.pagesCrawled} / {j.pagesDiscovered}</td>
                  <td data-label="Depth">{j.maxDepth}</td>
                  <td data-label="Duration">{fmtDuration(dur)}{running && dur !== null && <span className="muted"> (so far)</span>}</td>
                  <td className="row-actions">
                    <Link className="button-link" to={`/jobs/${j.jobId}`}>Open</Link>
                    <button type="button" disabled={rerun.pendingId !== null} onClick={() => rerun.rerun(j.jobId)}
                      title="Start a new job with the same URL and limits">
                      {rerun.pendingId === j.jobId ? 'Starting…' : 'Re-run'}
                    </button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}

      {data && data.totalCount > 0 && (
        <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} onChange={setPage} />
      )}
    </div>
  )
}
