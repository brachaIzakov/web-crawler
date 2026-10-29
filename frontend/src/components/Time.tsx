import { fmtAbsolute, fmtDate, fmtRelative } from '../format'

interface Props {
  iso: string | null
  /** When given, renders relative to this time ("3 min ago"). */
  now?: number
}

/** A timestamp whose tooltip always shows the full absolute date and time. */
export function Time({ iso, now }: Props) {
  if (!iso) return <span className="muted">—</span>
  return (
    <time dateTime={iso} title={fmtAbsolute(iso)}>
      {now === undefined ? fmtDate(iso) : fmtRelative(iso, now)}
    </time>
  )
}
