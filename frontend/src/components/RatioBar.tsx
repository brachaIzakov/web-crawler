import { fmtPercent } from '../format'

interface Props { ratio: number; internal: number; total: number }

/** Domain-link ratio as "75.5% (80/106)" with a tiny bar. */
export function RatioBar({ ratio, internal, total }: Props) {
  if (total === 0) return <span className="muted">no outgoing links</span>
  const pct = Math.max(0, Math.min(1, ratio)) * 100
  return (
    <span className="ratio" title={`Domain-link ratio: ${internal} of ${total} outgoing links stay on the starting domain`}>
      <span className="ratio-bar" aria-hidden="true"><span style={{ width: `${pct}%` }} /></span>
      {fmtPercent(ratio)} <span className="muted">({internal}/{total})</span>
    </span>
  )
}
