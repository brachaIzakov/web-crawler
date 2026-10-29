interface Props { percent: number; active?: boolean }

export function ProgressBar({ percent, active }: Props) {
  const p = Math.max(0, Math.min(100, percent))
  return (
    <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(p)}>
      <div className={`progress-fill${active ? ' active' : ''}`} style={{ width: `${p}%` }} />
    </div>
  )
}
