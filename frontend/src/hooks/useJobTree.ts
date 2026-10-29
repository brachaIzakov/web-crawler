import { useCallback, useEffect, useRef, useState } from 'react'
import { api, toError } from '../api/client'
import { isTerminal, type JobStatus, type TreeResponse } from '../api/types'

const THROTTLE_MS = 2000

export interface JobTreeState {
  tree: TreeResponse | null
  error: Error | null
  loading: boolean
  reload: () => void
}

interface TreeLoader {
  now: () => void
  throttled: () => void
  dispose: () => void
}

interface LoaderCallbacks {
  onStart: () => void
  onData: (tree: TreeResponse) => void
  onError: (error: Error) => void
}

/** Fetches the tree at most once per THROTTLE_MS; requests arriving meanwhile coalesce into one trailing fetch. */
function createTreeLoader(id: string, cb: LoaderCallbacks): TreeLoader {
  let ctrl: AbortController | null = null
  let timer: ReturnType<typeof setTimeout> | null = null
  let inFlight = false
  let dirty = false
  let lastStart = 0
  let disposed = false

  const clearTimer = () => {
    if (timer !== null) { clearTimeout(timer); timer = null }
  }

  function now() {
    if (disposed) return
    clearTimer()
    ctrl?.abort()
    const c = new AbortController()
    ctrl = c
    inFlight = true
    dirty = false
    lastStart = Date.now()
    cb.onStart()
    api.getTree(id, c.signal).then(
      (tree) => {
        if (c.signal.aborted) return
        inFlight = false
        cb.onData(tree)
        if (dirty) throttled()
      },
      (e: unknown) => {
        if (c.signal.aborted) return
        inFlight = false
        cb.onError(toError(e))
      },
    )
  }

  function throttled() {
    if (disposed) return
    if (inFlight) { dirty = true; return }
    if (timer !== null) return
    const wait = THROTTLE_MS - (Date.now() - lastStart)
    if (wait <= 0) now()
    else timer = setTimeout(() => { timer = null; now() }, wait)
  }

  return { now, throttled, dispose: () => { disposed = true; clearTimer(); ctrl?.abort() } }
}

/**
 * Loads the page tree once the job has started. While Running it refreshes whenever `progressKey`
 * changes (throttled to one fetch per 2 s); when the job reaches a terminal state it fetches once more.
 */
export function useJobTree(id: string, status: JobStatus | undefined, progressKey: string): JobTreeState {
  const [tree, setTree] = useState<TreeResponse | null>(null)
  const [error, setError] = useState<Error | null>(null)
  const [loading, setLoading] = useState(false)
  const loader = useRef<TreeLoader | null>(null)

  useEffect(() => {
    const l = createTreeLoader(id, {
      onStart: () => setLoading(true),
      onData: (t) => { setTree(t); setError(null); setLoading(false) },
      onError: (e) => { setError(e); setLoading(false) },
    })
    loader.current = l
    return () => { l.dispose(); loader.current = null }
  }, [id])

  const terminal = status !== undefined && isTerminal(status)
  const started = terminal || status === 'Running'
  useEffect(() => {
    const l = loader.current
    if (!l || !started) return
    if (terminal) l.now()
    else l.throttled()
  }, [started, terminal, progressKey])

  const reload = useCallback(() => loader.current?.now(), [])
  return { tree, error, loading, reload }
}
