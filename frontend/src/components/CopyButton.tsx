import { useEffect, useState } from 'react'
import { useMounted } from '../hooks/useMounted'

type CopyState = 'idle' | 'copied' | 'failed'

export function CopyButton({ text, label }: { text: string; label: string }) {
  const mounted = useMounted()
  const [state, setState] = useState<CopyState>('idle')

  useEffect(() => {
    if (state === 'idle') return
    const t = setTimeout(() => setState('idle'), 1500)
    return () => clearTimeout(t)
  }, [state])

  const copy = () => {
    // navigator.clipboard is undefined outside secure contexts (plain http on a non-localhost host).
    const write = navigator.clipboard?.writeText(text) ?? Promise.reject(new Error('Clipboard unavailable'))
    write.then(
      () => { if (mounted.current) setState('copied') },
      () => { if (mounted.current) setState('failed') },
    )
  }

  return (
    <button type="button" onClick={copy} aria-live="polite">
      {state === 'copied' ? 'Copied ✓' : state === 'failed' ? 'Copy failed' : label}
    </button>
  )
}
