import type { StreamMode } from '../hooks/useJobStream'

export function ConnectionIndicator({ mode }: { mode: StreamMode }) {
  return mode === 'live' ? (
    <span className="conn conn-live" title="Receiving live updates from the server (Server-Sent Events)">
      <span className="dot" aria-hidden="true" />Live
    </span>
  ) : (
    <span className="conn conn-polling" title="Live updates unavailable; refreshing every 1.5 s">
      <span className="dot" aria-hidden="true" />Polling
    </span>
  )
}
