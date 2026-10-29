import { useState } from 'react'
import { toError } from '../api/client'
import { useMounted } from '../hooks/useMounted'

type CancelState = 'idle' | 'confirm' | 'busy'

/** Cancel with an inline "are you sure" step. */
export function CancelButton({ onCancel }: { onCancel: () => Promise<void> }) {
  const mounted = useMounted()
  const [state, setState] = useState<CancelState>('idle')
  const [error, setError] = useState<Error | null>(null)

  const confirm = () => {
    setState('busy')
    setError(null)
    onCancel().then(
      () => { if (mounted.current) setState('idle') },
      (e: unknown) => { if (mounted.current) { setError(toError(e)); setState('idle') } },
    )
  }

  return (
    <span className="cancel-control">
      {state === 'idle' && (
        <button type="button" className="danger" onClick={() => setState('confirm')}>Cancel crawl</button>
      )}
      {state === 'confirm' && (
        <span className="confirm" role="group" aria-label="Confirm cancel">
          Cancel this crawl?
          <button type="button" className="danger" onClick={confirm}>Yes, cancel</button>
          <button type="button" onClick={() => setState('idle')}>No</button>
        </span>
      )}
      {state === 'busy' && <span className="muted">Canceling…</span>}
      {error && <span className="node-error" role="alert">Cancel failed: {error.message}</span>}
    </span>
  )
}
