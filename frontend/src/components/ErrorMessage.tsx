import { ApiError } from '../api/client'

interface Props { error: Error; onRetry?: () => void; title?: string }

export function ErrorMessage({ error, onRetry, title }: Props) {
  const fields = error instanceof ApiError ? error.problem?.errors : undefined
  const detail = error instanceof ApiError ? error.problem?.detail : undefined
  return (
    <div className="error-box" role="alert">
      {title && <div className="error-title">{title}</div>}
      <strong>{error.message}</strong>
      {detail && detail !== error.message && <div>{detail}</div>}
      {fields && (
        <ul>
          {Object.entries(fields).flatMap(([f, msgs]) => msgs.map((m) => <li key={f + m}>{f}: {m}</li>))}
        </ul>
      )}
      {onRetry && <div className="error-actions"><button type="button" onClick={onRetry}>Retry</button></div>}
    </div>
  )
}
