import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import { isTerminal, type JobDetails } from '../api/types'
import { usePolling } from './usePolling'

export type StreamMode = 'live' | 'polling'

export interface JobStreamState {
  job: JobDetails | null
  error: Error | null
  loading: boolean
  mode: StreamMode
  reload: () => void
}

const POLL_MS = 1500
/** Consecutive EventSource errors (without a job event in between) before falling back to polling. */
const MAX_SSE_FAILURES = 2

const stopPolling = (job: JobDetails | null, error: Error | null): boolean =>
  (job !== null && isTerminal(job.status)) || (error instanceof ApiError && error.status === 404)

/**
 * Live job updates over Server-Sent Events (`GET /api/jobs/{id}/events`), falling back to polling
 * `GET /api/jobs/{id}` every 1.5 s when EventSource is unavailable or the stream keeps failing.
 */
export function useJobStream(id: string): JobStreamState {
  const [mode, setMode] = useState<StreamMode>(() => (typeof EventSource === 'undefined' ? 'polling' : 'live'))
  const [liveJob, setLiveJob] = useState<JobDetails | null>(null)
  const [liveError, setLiveError] = useState<Error | null>(null)
  const [connection, setConnection] = useState(0)

  useEffect(() => {
    if (mode !== 'live') return
    let disposed = false
    let failures = 0
    let terminalSeen = false
    const es = new EventSource(api.jobEventsUrl(id))

    es.addEventListener('job', (ev) => {
      if (disposed) return
      failures = 0
      try {
        const job = JSON.parse(String(ev.data)) as JobDetails
        terminalSeen = isTerminal(job.status)
        setLiveJob(job)
        setLiveError(null)
      } catch {
        setLiveError(new Error('Received a malformed update from the server'))
      }
    })
    // Server sends `end` after the final snapshot and closes; close first so the browser doesn't reconnect.
    es.addEventListener('end', () => es.close())
    es.addEventListener('error', () => {
      if (disposed) return
      if (terminalSeen) { es.close(); return } // nothing more to receive
      failures += 1
      // CLOSED: the browser gave up (e.g. 404 / non-SSE response). Otherwise it is auto-retrying.
      if (es.readyState === EventSource.CLOSED || failures >= MAX_SSE_FAILURES) {
        es.close()
        setMode('polling')
      }
    })
    return () => { disposed = true; es.close() }
  }, [id, mode, connection])

  const polling = mode === 'polling'
  const poll = usePolling<JobDetails>(
    (signal) => api.getJob(id, signal),
    id,
    (d, e) => (stopPolling(d, e) ? null : POLL_MS),
    polling,
  )
  const { reload: pollReload } = poll

  const reload = useCallback(() => {
    if (polling) pollReload()
    else setConnection((c) => c + 1)
  }, [polling, pollReload])

  const job = polling ? poll.data ?? liveJob : liveJob
  const error = polling ? poll.error : liveError
  const loading = job === null && (polling ? poll.loading : liveError === null)
  return { job, error, loading, mode, reload }
}
