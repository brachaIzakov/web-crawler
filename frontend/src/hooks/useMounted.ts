import { useEffect, useRef, type RefObject } from 'react'

/** Ref that is true while the component is mounted; guards state updates after async work. */
export function useMounted(): RefObject<boolean> {
  const mounted = useRef(false)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])
  return mounted
}
