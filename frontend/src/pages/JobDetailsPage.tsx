import { useMemo } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError } from '../api/client'
import { isTerminal, type JobDetails } from '../api/types'
import { durationBetween, fmtCount, fmtDuration } from '../format'
import { useJobStream } from '../hooks/useJobStream'
import { useJobTree } from '../hooks/useJobTree'
import { useNow } from '../hooks/useNow'
import { useRerun } from '../hooks/useRerun'
import { computeStats } from '../tree'
import { CancelButton } from '../components/CancelButton'
import { ConnectionIndicator } from '../components/ConnectionIndicator'
import { CopyButton } from '../components/CopyButton'
import { ErrorMessage } from '../components/ErrorMessage'
import { ExternalLink } from '../components/ExternalLink'
import { Loading } from '../components/Loading'
import { Popover } from '../components/Popover'
import { ProgressBar } from '../components/ProgressBar'
import { StatusBadge } from '../components/StatusBadge'
import { Time } from '../components/Time'
import { TreeSummary } from '../components/TreeSummary'
import { TreeView } from '../components/TreeView'

export function JobDetailsPage() {
  const { id = '' } = useParams()
  // Keyed so that navigating to another job (e.g. after Re-run) starts with completely fresh state.
  return <JobDetailsView key={id} id={id} />
}

function NotFound({ id }: { id: string }) {
  return (
    <div className="card">
      <h1>Job not found</h1>
      <p>No job with id <span className="mono">{id}</span> exists.</p>
      <p><Link to="/">Start a crawl</Link> · <Link to="/history">History</Link></p>
    </div>
  )
}

/** One short sentence describing what the job is doing right now. */
function activityText(j: JobDetails, elapsedMs: number | null, depth: number): string {
  switch (j.status) {
    case 'Pending': return 'Queued — waiting for a worker to pick up the job…'
    case 'Running':
      if (j.pagesCrawled >= j.maxPages) return 'Page limit reached — finishing…'
      return depth >= 0 ? `Crawling depth ${depth}…` : 'Fetching the start page…'
    case 'Completed': return `Completed in ${fmtDuration(elapsedMs)}.`
    case 'Failed': return `Failed: ${j.failureReason ?? 'unknown error'}`
    case 'Canceled': return `Canceled after ${fmtCount(j.pagesCrawled)} page${j.pagesCrawled === 1 ? '' : 's'}; crawled pages are kept.`
  }
}

function JobDetailsView({ id }: { id: string }) {
  const { job, error, loading, mode, reload } = useJobStream(id)
  const tree = useJobTree(id, job?.status, job ? String(job.pagesCrawled) : '')
  const rerun = useRerun()
  const now = useNow(1000)
  const root = tree.tree?.root ?? null
  const stats = useMemo(() => computeStats(root), [root])

  if (!job) {
    if (error instanceof ApiError && error.status === 404) return <NotFound id={id} />
    if (error && !loading) return <ErrorMessage title="Couldn't load the job" error={error} onRetry={reload} />
    return <div className="card"><Loading label="Loading job…" /></div>
  }

  const terminal = isTerminal(job.status)
  const elapsedMs = terminal ? job.durationMs : durationBetween(job.startedAt, null, now)
  const queueMs = job.startedAt ? durationBetween(job.createdAt, job.startedAt, 0) : null
  const cancel = async () => { await api.cancelJob(id); reload() }
  const depth = stats.maxFetchedDepth
  const doRerun = () => rerun.rerun(job.jobId, { url: job.inputUrl, maxDepth: job.maxDepth, maxPages: job.maxPages })
  const rerunButton = (cls?: string) => (
    <button type="button" className={cls} disabled={rerun.pendingId !== null} onClick={doRerun}
      title="Start a new job with the same URL and limits">
      {rerun.pendingId ? 'Starting…' : 'Re-run'}
    </button>
  )

  const progressParts = [
    `${fmtCount(job.pagesCrawled)} of ${fmtCount(job.maxPages)} pages`,
    `${fmtCount(job.pagesDiscovered)} discovered`,
    depth >= 0 ? `depth ${depth} of ${job.maxDepth}` : null,
    elapsedMs !== null ? `${fmtDuration(elapsedMs)}${terminal ? '' : ' elapsed'}` : null,
  ].filter((p): p is string => p !== null)

  return (
    <>
      <div className="breadcrumb-row">
        <nav className="breadcrumb" aria-label="Breadcrumb">
          <Link to="/history">History</Link>
          <span className="breadcrumb-sep" aria-hidden="true">›</span>
          <span aria-current="page">Job <span className="mono" title={job.jobId}>{job.jobId.slice(0, 8)}…</span></span>
        </nav>
        <span className="spacer" />
        <Link to="/" className="button-link btn-quiet">+ New crawl</Link>
      </div>

      {/* 1. Status header */}
      <section className={`card status-card status-${job.status.toLowerCase()}`} aria-label="Job status">
        <div className="status-head">
          <div className="status-left">
            <span className="status-big"><StatusBadge status={job.status} /></span>
            {!terminal && <ConnectionIndicator mode={mode} />}
          </div>
          <h1 className="status-url">
            <span className="sr-only">Crawl of </span>
            <ExternalLink className="mono" url={job.inputUrl} />
          </h1>
          <div className="status-actions">
            {!terminal && <CancelButton onCancel={cancel} />}
            {terminal && rerunButton('primary')}
            <Popover label="⋯ More" ariaLabel="More actions" buttonClassName="btn-quiet">
              <div className="menu" role="group" aria-label="More actions">
                {!terminal && rerunButton()}
                <CopyButton text={job.jobId} label="Copy job id" />
                <a className="button-link" href={api.jobJsonUrl(job.jobId)} target="_blank" rel="noopener noreferrer">Raw JSON</a>
              </div>
            </Popover>
          </div>
        </div>

        {job.status === 'Failed' && (
          <div className="error-box failure" role="alert">
            <div className="error-title">Failure reason</div>
            <strong>{job.failureReason ?? 'No reason was reported.'}</strong>
          </div>
        )}
        {rerun.error && <ErrorMessage title="Re-run failed" error={rerun.error} />}

        <div className="progress-block">
          <ProgressBar percent={job.progressPercent} active={!terminal} />
          <p className="progress-text">
            <span>{progressParts.join(' · ')}</span>
            {job.status !== 'Failed' && <span className="activity">{activityText(job, elapsedMs, depth)}</span>}
          </p>
        </div>
        {error && <p className="muted" role="status">Update failed: {error.message} — retrying automatically.</p>}
      </section>

      {/* 2. Results summary */}
      {root && <TreeSummary stats={stats} live={!terminal} />}

      {/* 3. Page tree */}
      <section className="card" aria-labelledby="tree-heading">
        <div className="card-head">
          <h2 id="tree-heading">Page tree</h2>
          {tree.loading && tree.tree && <span className="muted hint">refreshing…</span>}
        </div>
        {job.status === 'Pending' ? (
          <p className="muted">The tree appears once crawling starts.</p>
        ) : (
          <>
            {tree.error && <ErrorMessage title="Couldn't load the page tree" error={tree.error} onRetry={tree.reload} />}
            {tree.tree && <TreeView tree={tree.tree} />}
            {!tree.tree && !tree.error && <Loading label="Loading page tree…" lines={5} />}
          </>
        )}
      </section>

      {/* 4. Reference details: collapsed while running, expanded once finished (remounted on the switch). */}
      <details key={terminal ? 'done' : 'running'} className="card job-details" open={terminal}>
        <summary><h2>Job details</h2></summary>
        <dl className="kv">
          <dt>Job id</dt><dd className="mono">{job.jobId}</dd>
          <dt>Starting domain</dt><dd className="mono">{job.startingDomain}</dd>
          <dt>Limits</dt><dd>max depth {job.maxDepth} · max pages {job.maxPages}</dd>
          <dt>Created</dt><dd><Time iso={job.createdAt} /></dd>
          <dt>Started</dt>
          <dd><Time iso={job.startedAt} />{queueMs !== null && <span className="muted"> (queued {fmtDuration(queueMs)})</span>}</dd>
          <dt>Finished</dt><dd><Time iso={job.completedAt} /></dd>
          <dt>Duration</dt><dd>{fmtDuration(elapsedMs)}{!terminal && job.startedAt && <span className="muted"> (so far)</span>}</dd>
        </dl>
      </details>
    </>
  )
}
