import { useCallback, useEffect, useRef, useState } from 'react'
import { toError } from '../api/client'
import { useInterval } from './useInterval'

export interface PollingState<T> {
  data: T | null
  error: Error | null
  /** True only until the first response for the current key arrives. */
  loading: boolean
  reload: () => void
}

/** Fixed interval, null to stop, or a function deriving the interval from the latest result. */
export type PollInterval<T> = number | null | ((data: T | null, error: Error | null) => number | null)

/**
 * Fetches when enabled / when `key` changes, then every interval while the interval is non-null.
 * A key change resets the state; reload() and interval ticks keep the last good data on screen.
 * Interval ticks are skipped while a request is still in flight.
 */
export function usePolling<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  key: string,
  interval: PollInterval<T>,
  enabled = true,
): PollingState<T> {
  // Results are tagged with the key they belong to, so a key change shows no stale data.
  const [result, setResult] = useState<{ key: string; data: T | null; error: Error | null } | null>(null)
  const fetcherRef = useRef(fetcher)
  useEffect(() => { fetcherRef.current = fetcher }, [fetcher])
  const ctrl = useRef<AbortController | null>(null)
  const inFlight = useRef(false)

  const run = useCallback((forKey: string) => {
    ctrl.current?.abort()
    const c = new AbortController()
    ctrl.current = c
    inFlight.current = true
    const settle = () => { inFlight.current = false }
    fetcherRef.current(c.signal).then(
      (d) => {
        if (c.signal.aborted) return
        setResult({ key: forKey, data: d, error: null })
        settle()
      },
      (e: unknown) => {
        if (c.signal.aborted) return
        // Keep the last good data for this key; just report the error.
        setResult((prev) => ({ key: forKey, data: prev?.key === forKey ? prev.data : null, error: toError(e) }))
        settle()
      },
    )
  }, [])

  // Key change (or enabling): start fresh.
  useEffect(() => {
    if (!enabled) return
    run(key)
    return () => { ctrl.current?.abort(); inFlight.current = false }
  }, [key, enabled, run])

  const current = result?.key === key ? result : null
  const data = current?.data ?? null
  const error = current?.error ?? null
  const delay = !enabled ? null : typeof interval === 'function' ? interval(data, error) : interval
  useInterval(() => { if (!inFlight.current) run(key) }, delay)

  const reload = useCallback(() => { if (enabled) run(key) }, [enabled, key, run])
  return { data, error, loading: enabled && current === null, reload }
}
