/** Parses an API timestamp; timestamps without an offset are treated as UTC (the API stores UTC). */
export function parseIso(iso: string): Date {
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(iso)
  return new Date(hasZone ? iso : `${iso}Z`)
}

export function fmtDate(iso: string | null): string {
  if (!iso) return '—'
  const d = parseIso(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString()
}

/** Long, unambiguous form used for tooltips. */
export function fmtAbsolute(iso: string): string {
  const d = parseIso(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'long' })
}

export function fmtRelative(iso: string, now: number): string {
  const t = parseIso(iso).getTime()
  if (isNaN(t)) return iso
  const s = Math.round((now - t) / 1000)
  if (s < 45) return 'just now'
  const m = Math.round(s / 60)
  if (m < 60) return `${m} min ago`
  const h = Math.round(m / 60)
  if (h < 24) return `${h} h ago`
  const d = Math.round(h / 24)
  if (d < 7) return `${d} day${d === 1 ? '' : 's'} ago`
  return fmtDate(iso)
}

export function fmtDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '—'
  if (ms < 1000) return `${Math.round(ms)} ms`
  const s = ms / 1000
  if (s < 60) return `${s.toFixed(1)} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${Math.round(s % 60)}s`
  return `${Math.floor(m / 60)}h ${m % 60}m`
}

/** Milliseconds between two timestamps; an open end is measured up to `now`. Null if never started. */
export function durationBetween(start: string | null, end: string | null, now: number): number | null {
  if (!start) return null
  const from = parseIso(start).getTime()
  const to = end ? parseIso(end).getTime() : now
  return isNaN(from) || isNaN(to) ? null : Math.max(0, to - from)
}

export function fmtPercent(ratio: number): string {
  return `${(ratio * 100).toFixed(1)}%`
}

export function fmtCount(n: number): string {
  return n.toLocaleString()
}

/** Returns the URL only if it is http(s), so crawled data can never inject e.g. a javascript: link. */
export function safeHref(url: string): string | undefined {
  return /^https?:\/\//i.test(url) ? url : undefined
}
