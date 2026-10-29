import { useCallback, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, toError } from '../api/client'
import type { CreateJobRequest } from '../api/types'
import { useMounted } from './useMounted'

export interface RerunState {
  /** Id of the job being re-run, if any. */
  pendingId: string | null
  error: Error | null
  rerun: (jobId: string, params?: CreateJobRequest) => void
}

/**
 * Starts a new job with the same url / maxDepth / maxPages as an existing one and navigates to it.
 * When params aren't known (history rows lack maxPages) the source job is fetched first.
 */
export function useRerun(): RerunState {
  const navigate = useNavigate()
  const mounted = useMounted()
  const [pendingId, setPendingId] = useState<string | null>(null)
  const [error, setError] = useState<Error | null>(null)

  const rerun = useCallback((jobId: string, params?: CreateJobRequest) => {
    setPendingId(jobId)
    setError(null)
    void (async () => {
      try {
        let req: CreateJobRequest
        if (params) req = params
        else {
          const j = await api.getJob(jobId)
          req = { url: j.inputUrl, maxDepth: j.maxDepth, maxPages: j.maxPages }
        }
        const created = await api.createJob(req)
        if (mounted.current) navigate(`/jobs/${created.jobId}`)
      } catch (e) {
        if (!mounted.current) return
        setError(toError(e))
        setPendingId(null)
      }
    })()
  }, [navigate, mounted])

  return { pendingId, error, rerun }
}
