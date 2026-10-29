import { fmtCount, fmtPercent } from '../format'
import type { TreeStats } from '../tree'

/** One row of result tiles; rendered muted while the crawl is still running. */
export function TreeSummary({ stats, live }: { stats: TreeStats; live: boolean }) {
  const s = stats.byStatus
  const tiles: [string, string, string][] = [
    ['crawled', 'Crawled', fmtCount(s.Crawled)],
    ['failed', 'Failed', fmtCount(s.Failed)],
    ['skipped', 'Skipped', fmtCount(s.Skipped)],
    ['discovered', 'Not crawled', fmtCount(s.Discovered)],
    ['ratio', 'Avg domain ratio', stats.avgRatio === null ? '—' : fmtPercent(stats.avgRatio)],
  ]
  return (
    <section className={`summary${live ? ' is-live' : ''}`} aria-label="Results summary">
      <dl className="stat-tiles">
        {tiles.map(([key, label, value]) => (
          <div key={key} className={`stat stat-${key}`}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}
