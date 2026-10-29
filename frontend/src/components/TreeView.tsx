import { memo, useCallback, useMemo, useState } from 'react'
import { PAGE_STATUSES, type PageStatus, type TreeNodeDto, type TreeResponse } from '../api/types'
import { buildView, isFiltering } from '../tree'
import { TreeGraph } from './TreeGraph'
import { TreeLegend } from './TreeLegend'
import { TreeNode } from './TreeNode'

const STATUS_LABEL: Record<PageStatus, string> = {
  Crawled: 'Crawled', Failed: 'Failed', Skipped: 'Skipped', Discovered: 'Not crawled',
}
const DEFAULT_OPEN_DEPTH = 1

type ViewMode = 'list' | 'graph'

interface Props { tree: TreeResponse }

/** Memoised: the job page re-renders every second for the elapsed clock; the tree only changes on refresh. */
export const TreeView = memo(function TreeView({ tree }: Props) {
  const [mode, setMode] = useState<ViewMode>('list')
  const [query, setQuery] = useState('')
  const [statuses, setStatuses] = useState<ReadonlySet<PageStatus>>(() => new Set(PAGE_STATUSES))
  // Expansion: per-node overrides on top of a default (expand all / collapse all / depth-based).
  const [allOpen, setAllOpen] = useState<boolean | null>(null)
  const [overrides, setOverrides] = useState<ReadonlyMap<number, boolean>>(() => new Map())

  const filter = useMemo(() => ({ query, statuses }), [query, statuses])
  const filtering = isFiltering(filter)
  const view = useMemo(() => (tree.root ? buildView(tree.root, filter) : null), [tree.root, filter])

  const resetExpansion = (open: boolean | null) => { setAllOpen(open); setOverrides(new Map()) }
  const isOpen = useCallback(
    (n: TreeNodeDto) => overrides.get(n.pageId) ?? allOpen ?? (filtering || n.depth <= DEFAULT_OPEN_DEPTH),
    [overrides, allOpen, filtering],
  )
  const onToggle = useCallback((n: TreeNodeDto, open: boolean) => {
    setOverrides((m) => new Map(m).set(n.pageId, open))
  }, [])

  const toggleStatus = (s: PageStatus) => {
    setStatuses((prev) => {
      const next = new Set(prev)
      if (next.has(s)) next.delete(s)
      else next.add(s)
      return next
    })
    resetExpansion(null)
  }
  const clearFilters = () => { setQuery(''); setStatuses(new Set(PAGE_STATUSES)); resetExpansion(null) }

  return (
    <div className="tree-view">
      <div className="tree-toolbar" role="toolbar" aria-label="Tree controls">
        <div className="segmented" role="group" aria-label="Tree view mode">
          {(['list', 'graph'] as const).map((m) => (
            <button key={m} type="button" aria-pressed={mode === m} onClick={() => setMode(m)}>
              {m === 'list' ? 'List' : 'Graph'}
            </button>
          ))}
        </div>
        <input
          type="search"
          className="tree-search"
          placeholder="Filter by URL…"
          aria-label="Filter pages by URL"
          value={query}
          onChange={(e) => { setQuery(e.target.value); resetExpansion(null) }}
        />
        <div className="chips" role="group" aria-label="Show statuses">
          {PAGE_STATUSES.map((s) => (
            <button
              key={s}
              type="button"
              className={`chip chip-${s.toLowerCase()}`}
              aria-pressed={statuses.has(s)}
              onClick={() => toggleStatus(s)}
            >
              {STATUS_LABEL[s]}
            </button>
          ))}
          {filtering && <button type="button" className="chip chip-clear" onClick={clearFilters}>Clear ×</button>}
        </div>
        <span className="spacer" />
        <div className="tree-toolbar-end">
          <button type="button" className="btn-quiet" onClick={() => resetExpansion(true)}>Expand all</button>
          <button type="button" className="btn-quiet" onClick={() => resetExpansion(false)}>Collapse all</button>
          <TreeLegend />
        </div>
      </div>
      {!tree.root && <p className="muted">No pages yet.</p>}
      {tree.root && !view && <p className="muted">No pages match the current filters.</p>}
      {view && mode === 'list' && (
        <ul className="tree root">
          <TreeNode jobId={tree.jobId} view={view} isOpen={isOpen} onToggle={onToggle} />
        </ul>
      )}
      {view && mode === 'graph' && <TreeGraph jobId={tree.jobId} view={view} isOpen={isOpen} onToggle={onToggle} />}
    </div>
  )
})
