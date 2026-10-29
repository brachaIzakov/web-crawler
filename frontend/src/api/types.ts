export type JobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled'
export type PageStatus = 'Discovered' | 'Crawled' | 'Failed' | 'Skipped'

export interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
  errors?: Record<string, string[]>
}

export interface CreateJobRequest {
  url: string
  maxDepth?: number
  maxPages?: number
}

export interface JobSummary {
  jobId: string
  inputUrl: string
  status: JobStatus
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  pagesCrawled: number
  pagesDiscovered: number
  maxDepth: number
}

export interface JobListResponse {
  items: JobSummary[]
  page: number
  pageSize: number
  totalCount: number
}

export interface JobDetails {
  jobId: string
  inputUrl: string
  startingDomain: string
  maxDepth: number
  maxPages: number
  status: JobStatus
  failureReason: string | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  pagesCrawled: number
  pagesDiscovered: number
  progressPercent: number
  durationMs: number | null
}

export interface TreeNodeDto {
  pageId: number
  url: string
  depth: number
  status: PageStatus
  httpStatusCode: number | null
  title: string | null
  outgoingLinkCount: number
  internalLinkCount: number
  domainLinkRatio: number
  error: string | null
  /** Set when the server redirected; the URL the content was actually fetched from. */
  finalUrl: string | null
  children: TreeNodeDto[]
}

export interface TreeResponse {
  jobId: string
  totalPages: number
  root: TreeNodeDto | null
}

export interface PageLink {
  url: string
  isInternal: boolean
}

export interface PageLinksResponse {
  pageId: number
  url: string
  outgoingLinkCount: number
  internalLinkCount: number
  domainLinkRatio: number
  links: PageLink[]
}

export const PAGE_STATUSES: readonly PageStatus[] = ['Crawled', 'Failed', 'Skipped', 'Discovered']

export const isTerminal = (s: JobStatus): boolean =>
  s === 'Completed' || s === 'Failed' || s === 'Canceled'
