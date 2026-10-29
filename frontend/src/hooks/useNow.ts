import { useState } from 'react'
import { useInterval } from './useInterval'

/** Current time in ms, refreshed every intervalMs (for relative times and running durations). */
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useInterval(() => setNow(Date.now()), intervalMs)
  return now
}
