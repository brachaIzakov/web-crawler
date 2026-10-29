import type { JobStatus, PageStatus } from '../api/types'

const LABELS: Partial<Record<JobStatus | PageStatus, string>> = { Discovered: 'Not crawled' }

export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  return (
    <span className={`badge badge-${status.toLowerCase()}`} title={status === 'Discovered' ? 'Discovered but not crawled' : undefined}>
      {LABELS[status] ?? status}
    </span>
  )
}
