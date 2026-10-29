import type {
  CreateJobRequest, JobDetails, JobListResponse, PageLinksResponse, ProblemDetails, TreeResponse, JobStatus,
} from './types'

export class ApiError extends Error {
  status: number
  problem: ProblemDetails | null
  constructor(status: number, problem: ProblemDetails | null, message?: string) {
    super(message ?? problem?.title ?? problem?.detail ?? `Request failed (${status})`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }
}

export const toError = (e: unknown): Error => (e instanceof Error ? e : new Error(String(e)))

async function request<T>(path: string, init?: RequestInit, signal?: AbortSignal): Promise<T> {
  let res: Response
  try {
    res = await fetch(`/api${path}`, {
      ...init,
      signal,
      headers: { Accept: 'application/json', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new ApiError(0, null, 'Network error: could not reach the server')
  }
  const text = await res.text()
  let body: unknown = null
  if (text) {
    try { body = JSON.parse(text) } catch { body = null }
  }
  if (!res.ok) {
    throw new ApiError(res.status, (body as ProblemDetails | null) ?? null, body ? undefined : `Request failed (${res.status})`)
  }
  return body as T
}

export const api = {
  createJob: (req: CreateJobRequest) =>
    request<{ jobId: string }>('/jobs', { method: 'POST', body: JSON.stringify(req) }),
  listJobs: (page: number, pageSize: number, signal?: AbortSignal) =>
    request<JobListResponse>(`/jobs?page=${page}&pageSize=${pageSize}`, undefined, signal),
  getJob: (id: string, signal?: AbortSignal) =>
    request<JobDetails>(`/jobs/${encodeURIComponent(id)}`, undefined, signal),
  getTree: (id: string, signal?: AbortSignal) =>
    request<TreeResponse>(`/jobs/${encodeURIComponent(id)}/tree`, undefined, signal),
  getPageLinks: (jobId: string, pageId: number, signal?: AbortSignal) =>
    request<PageLinksResponse>(`/jobs/${encodeURIComponent(jobId)}/pages/${pageId}/links`, undefined, signal),
  cancelJob: (id: string) =>
    request<{ jobId: string; status: JobStatus }>(`/jobs/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),
  /** Server-Sent Events stream of JobDetails snapshots. */
  jobEventsUrl: (id: string) => `/api/jobs/${encodeURIComponent(id)}/events`,
  /** Raw JSON of the job, for the "view JSON" link. */
  jobJsonUrl: (id: string) => `/api/jobs/${encodeURIComponent(id)}`,
}
