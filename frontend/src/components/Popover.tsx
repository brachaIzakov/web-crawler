import { useEffect, useId, useRef, useState, type ReactNode } from 'react'

interface Props {
  /** Button content. */
  label: ReactNode
  /** Accessible name when the label is terse (e.g. "⋯"). */
  ariaLabel?: string
  buttonClassName?: string
  align?: 'left' | 'right'
  children: ReactNode
}

/** A button that toggles a small floating panel; closes on outside click or Escape. */
export function Popover({ label, ariaLabel, buttonClassName, align = 'right', children }: Props) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const panelId = useId()

  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <div className="popover" ref={root}>
      <button
        type="button"
        className={buttonClassName}
        aria-label={ariaLabel}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((o) => !o)}
      >
        {label}
      </button>
      {open && (
        <div id={panelId} className={`popover-panel popover-${align}`}>
          {children}
        </div>
      )}
    </div>
  )
}
