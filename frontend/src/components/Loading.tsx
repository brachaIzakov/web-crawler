interface Props { label?: string; lines?: number }

export function Loading({ label = 'Loading…', lines = 3 }: Props) {
  return (
    <div className="loading" role="status" aria-live="polite">
      <span className="muted">{label}</span>
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="skeleton" style={{ width: `${90 - i * 20}%` }} />
      ))}
    </div>
  )
}
