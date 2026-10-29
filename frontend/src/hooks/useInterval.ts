import { useEffect, useRef } from 'react'

/** Calls callback every delayMs; pass null to pause. */
export function useInterval(callback: () => void, delayMs: number | null): void {
  const saved = useRef(callback)
  useEffect(() => { saved.current = callback }, [callback])
  useEffect(() => {
    if (delayMs === null) return
    const id = setInterval(() => saved.current(), delayMs)
    return () => clearInterval(id)
  }, [delayMs])
}
