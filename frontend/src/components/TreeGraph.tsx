import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { PageStatus, TreeNodeDto } from '../api/types'
import { fmtPercent } from '../format'
import type { ViewNode } from '../tree'
import { ExternalLink } from './ExternalLink'
import { RatioBar } from './RatioBar'
import { StatusBadge } from './StatusBadge'
import { LinksPanel } from './TreeNode'

const NODE_H = 30
const LEVEL_H = 76 // vertical distance between depth levels (node + room for the curve and the +N badge)
const H_GAP = 14 // horizontal gap between neighbouring subtrees
const PAD = 16
const BADGE_H = 16
/** Nodes with more visible children than this lay the leaf/collapsed ones out in a grid. */
const GRID_MAX = 8
const GRID_ROW_GAP = 40
/** Above this many visible nodes the graph switches to compact nodes so it stays balanced. */
const COMPACT_THRESHOLD = 150

interface Metrics { nodeW: number; maxChars: number; maxCharsWithRatio: number; showRatio: boolean }
const NORMAL: Metrics = { nodeW: 150, maxChars: 18, maxCharsWithRatio: 14, showRatio: true }
const COMPACT: Metrics = { nodeW: 110, maxChars: 12, maxCharsWithRatio: 12, showRatio: false }

const STATUS_COLOR: Record<PageStatus, string> = {
  Crawled: '#16a34a', Failed: '#dc2626', Skipped: '#9ca3af', Discovered: '#f59e0b',
}

interface GNode {
  view: ViewNode
  /** Left edge of the node box. */
  x: number
  /** Top edge of the node box. */
  y: number
  open: boolean
  /** Number of visible children hidden because the node is collapsed. */
  hidden: number
}
interface GEdge { from: GNode; to: GNode }
interface Layout { nodes: GNode[]; edges: GEdge[]; width: number; height: number; m: Metrics }

function countVisible(v: ViewNode, isOpen: (n: TreeNodeDto) => boolean): number {
  let n = 1
  if (v.children.length > 0 && isOpen(v.node)) for (const c of v.children) n += countVisible(c, isOpen)
  return n
}

/**
 * Top-down tidy tree with grids for wide fan-outs. Depth maps to y.
 *
 * Pass 1 (post-order) measures every visible subtree (width and height):
 *  - leaf / collapsed node: one slot (nodeW + gap) wide, NODE_H tall;
 *  - open node with ≤ GRID_MAX children: children side by side, width = max(slot, Σ child widths);
 *  - open node with > GRID_MAX children: children that are themselves expanded ("blocks") go first,
 *    side by side in the normal tidy way; the remaining leaf/collapsed children fill a grid of at most
 *    GRID_MAX per row below them, rows GRID_ROW_GAP apart. Width = max(slot, blocks row, grid row).
 * Pass 2 places children inside their parent's slot (each band centred when the slot is wider). A normal
 * parent sits at the midpoint of its first and last child; a grid parent is centred over its whole block.
 * The drawing is as wide as the root subtree, so the root is centred over everything below it.
 */
function layout(root: ViewNode, isOpen: (n: TreeNodeDto) => boolean): Layout {
  const m = countVisible(root, isOpen) > COMPACT_THRESHOLD ? COMPACT : NORMAL
  const slot = m.nodeW + H_GAP
  const rowPitch = NODE_H + GRID_ROW_GAP
  const isExpanded = (v: ViewNode) => v.children.length > 0 && isOpen(v.node)

  interface Box { w: number; h: number; grid: boolean; blocks: ViewNode[]; leaves: ViewNode[]; blocksW: number; blocksH: number }
  const boxes = new Map<ViewNode, Box>()
  const box = (v: ViewNode): Box => boxes.get(v)!

  const measure = (v: ViewNode): Box => {
    const b: Box = { w: slot, h: NODE_H, grid: false, blocks: [], leaves: [], blocksW: 0, blocksH: 0 }
    if (isExpanded(v)) {
      for (const c of v.children) measure(c)
      if (v.children.length > GRID_MAX) {
        b.grid = true
        for (const c of v.children) (isExpanded(c) ? b.blocks : b.leaves).push(c)
      } else {
        b.blocks = v.children
      }
      for (const c of b.blocks) {
        b.blocksW += box(c).w
        b.blocksH = Math.max(b.blocksH, box(c).h)
      }
      const cols = Math.min(GRID_MAX, b.leaves.length)
      const rows = Math.ceil(b.leaves.length / GRID_MAX)
      const gridH = rows > 0 ? rows * rowPitch - GRID_ROW_GAP : 0
      b.w = Math.max(slot, b.blocksW, cols * slot)
      b.h = LEVEL_H + b.blocksH + (b.blocksH > 0 && rows > 0 ? GRID_ROW_GAP : 0) + gridH
    }
    boxes.set(v, b)
    return b
  }
  const rootBox = measure(root)

  const nodes: GNode[] = []
  const edges: GEdge[] = []
  const place = (v: ViewNode, left: number, top: number): GNode => {
    const open = isExpanded(v)
    const g: GNode = { view: v, x: 0, y: top, open, hidden: open ? 0 : v.children.length }
    nodes.push(g)
    const b = box(v)
    let center = left + b.w / 2
    if (open) {
      const childTop = top + LEVEL_H
      const kids: GNode[] = []
      let cursor = left + (b.w - b.blocksW) / 2
      for (const c of b.blocks) {
        kids.push(place(c, cursor, childTop))
        cursor += box(c).w
      }
      if (b.leaves.length > 0) {
        const cols = Math.min(GRID_MAX, b.leaves.length)
        const gx = left + (b.w - cols * slot) / 2
        const gy = childTop + (b.blocksH > 0 ? b.blocksH + GRID_ROW_GAP : 0)
        b.leaves.forEach((c, i) => {
          kids.push(place(c, gx + (i % GRID_MAX) * slot, gy + Math.floor(i / GRID_MAX) * rowPitch))
        })
      }
      if (!b.grid) center = (kids[0].x + kids[kids.length - 1].x) / 2 + m.nodeW / 2
      for (const k of kids) edges.push({ from: g, to: k })
    }
    g.x = center - m.nodeW / 2
    return g
  }
  // Offset by PAD minus half a gap so the outermost nodes get an equal PAD margin.
  place(root, PAD - H_GAP / 2, PAD)
  return {
    nodes,
    edges,
    width: rootBox.w + 2 * PAD - H_GAP,
    height: PAD * 2 + rootBox.h + BADGE_H / 2,
    m,
  }
}

function hostOf(url: string): string | null {
  try { return new URL(url).host } catch { return null }
}

/** Short label: "/" for the root, the host for cross-host pages, otherwise the last path segment. */
function shortLabel(url: string, rootHost: string | null, isRoot: boolean): string {
  if (isRoot) return '/'
  try {
    const u = new URL(url)
    if (u.host !== rootHost) return u.host
    const segs = u.pathname.split('/').filter((s) => s !== '')
    let last = segs.length > 0 ? segs[segs.length - 1] : '/'
    try { last = decodeURIComponent(last) } catch { /* keep encoded */ }
    return `${last}${u.search}`
  } catch {
    return url
  }
}

const truncate = (text: string, max: number): string => (text.length > max ? `${text.slice(0, max - 1)}…` : text)

const hasRatio = (n: TreeNodeDto): boolean => n.status === 'Crawled' && n.outgoingLinkCount > 0

/** Full details of a node: used by the hover tooltip and the selection strip. */
function NodeFacts({ n }: { n: TreeNodeDto }) {
  return (
    <>
      <span className="facts-item"><StatusBadge status={n.status} /></span>
      {n.httpStatusCode !== null && (
        <span className={`facts-item mono${n.httpStatusCode >= 400 ? ' http-bad' : ''}`}>HTTP {n.httpStatusCode}</span>
      )}
      <span className="facts-item">depth {n.depth}</span>
      {n.status === 'Crawled' && (
        <span className="facts-item"><RatioBar ratio={n.domainLinkRatio} internal={n.internalLinkCount} total={n.outgoingLinkCount} /></span>
      )}
      {n.children.length > 0 && (
        <span className="facts-item">{n.children.length} {n.children.length === 1 ? 'child' : 'children'}</span>
      )}
    </>
  )
}

interface Props {
  jobId: string
  view: ViewNode
  isOpen: (node: TreeNodeDto) => boolean
  onToggle: (node: TreeNodeDto, open: boolean) => void
}

export function TreeGraph({ jobId, view, isOpen, onToggle }: Props) {
  const { nodes, edges, width, height, m } = useMemo(() => layout(view, isOpen), [view, isOpen])
  const rootHost = hostOf(view.node.url)
  const [hoverId, setHoverId] = useState<number | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [linksFor, setLinksFor] = useState<number | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  /** Size the viewport was last auto-centred for; null forces a re-centre. */
  const centeredFor = useRef<string | null>(null)
  /** Scroll position we set ourselves, to tell our own scroll events apart from the user's. */
  const autoPos = useRef<{ left: number; top: number } | null>(null)
  const userScrolled = useRef(false)
  /** Page id to bring into view after the next layout (set on click/keyboard selection). */
  const revealId = useRef<number | null>(null)

  // Look nodes up by id so hover/selection survive live tree refreshes (new objects, same pageIds).
  const byId = useMemo(() => new Map(nodes.map((g) => [g.view.node.pageId, g])), [nodes])
  const hovered = hoverId !== null ? byId.get(hoverId) : undefined
  const selected = selectedId !== null ? byId.get(selectedId) : undefined

  const rootNode = nodes[0]
  // On first render and whenever the drawing resizes, centre the root horizontally at the top - unless
  // the user has scrolled the graph themselves (so live refreshes never yank their view away).
  useLayoutEffect(() => {
    const el = scrollRef.current
    if (!el || !rootNode) return
    const key = `${width}x${height}`
    if (centeredFor.current !== key && !userScrolled.current) {
      const left = Math.max(0, rootNode.x + m.nodeW / 2 - el.clientWidth / 2)
      const top = Math.max(0, rootNode.y - 12)
      el.scrollLeft = left
      el.scrollTop = top
      autoPos.current = { left: el.scrollLeft, top: el.scrollTop }
    }
    centeredFor.current = key
    if (revealId.current !== null) {
      const target = el.querySelector(`[data-pid="${revealId.current}"]`)
      revealId.current = null
      target?.scrollIntoView({ block: 'nearest', inline: 'center' })
    }
  }, [width, height, rootNode, m.nodeW, nodes])

  const onScroll = () => {
    const el = scrollRef.current
    const a = autoPos.current
    if (!el) return
    if (a && Math.abs(el.scrollLeft - a.left) < 2 && Math.abs(el.scrollTop - a.top) < 2) return
    userScrolled.current = true
  }

  const activate = (g: GNode) => {
    setSelectedId(g.view.node.pageId)
    revealId.current = g.view.node.pageId
    if (g.view.children.length > 0) onToggle(g.view.node, !g.open)
  }
  const onKey = (e: KeyboardEvent<SVGGElement>, g: GNode) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      activate(g)
    } else if (e.key === 'Escape') {
      setSelectedId(null)
    }
  }

  // The tooltip is position: fixed (outside the scroll box, so it is never clipped) and placed from the
  // hovered node's on-screen rect: below the node, flipped above when it would overflow the viewport
  // bottom, and clamped horizontally, all with an 8 px margin.
  const tipRef = useRef<HTMLDivElement>(null)
  const positionTip = useCallback(() => {
    const tip = tipRef.current
    const anchor = hoverId !== null ? scrollRef.current?.querySelector(`[data-pid="${hoverId}"]`) : null
    if (!tip || !anchor) return
    const a = anchor.getBoundingClientRect()
    const t = tip.getBoundingClientRect()
    const vw = document.documentElement.clientWidth
    const vh = document.documentElement.clientHeight
    const M = 8
    const left = Math.max(M, Math.min(vw - M - t.width, a.left + a.width / 2 - t.width / 2))
    let top = a.bottom + M
    if (top + t.height > vh - M && a.top - M - t.height >= M) top = a.top - M - t.height
    top = Math.max(M, Math.min(vh - M - t.height, top))
    tip.style.left = `${left}px`
    tip.style.top = `${top}px`
    tip.style.visibility = 'visible'
  }, [hoverId])
  useLayoutEffect(positionTip, [positionTip, nodes])
  useEffect(() => {
    if (hoverId === null) return
    window.addEventListener('scroll', positionTip, true)
    window.addEventListener('resize', positionTip)
    return () => {
      window.removeEventListener('scroll', positionTip, true)
      window.removeEventListener('resize', positionTip)
    }
  }, [hoverId, positionTip])

  const tooltip = hovered && (
    <div ref={tipRef} className="graph-tip" role="tooltip" style={{ left: 0, top: 0, visibility: 'hidden' }}>
      <div className="mono tip-url">{hovered.view.node.url}</div>
      {hovered.view.node.title && <div className="tip-title">“{hovered.view.node.title}”</div>}
      <div className="facts"><NodeFacts n={hovered.view.node} /></div>
    </div>
  )

  const sel = selected?.view.node
  return (
    <div className="graph-wrap">
      <div className="graph-scroll" ref={scrollRef} onScroll={onScroll}>
        <div className="graph-canvas" style={{ width, height }}>
          <svg
            className="graph"
            width={width}
            height={height}
            viewBox={`0 0 ${width} ${height}`}
            role="tree"
            aria-label="Page tree graph"
          >
            <g className="graph-edges" aria-hidden="true">
              {edges.map(({ from, to }) => {
                const x1 = from.x + m.nodeW / 2
                const y1 = from.y + NODE_H
                const x2 = to.x + m.nodeW / 2
                const y2 = to.y
                const my = (y1 + y2) / 2
                const grid = to.y > from.y + LEVEL_H + 1
                return <path key={to.view.node.pageId} className={grid ? 'edge-grid' : undefined} d={`M${x1},${y1} C${x1},${my} ${x2},${my} ${x2},${y2}`} />
              })}
            </g>
            {nodes.map((g) => {
              const n = g.view.node
              const hasKids = g.view.children.length > 0
              const ratio = m.showRatio && hasRatio(n)
              const text = truncate(shortLabel(n.url, rootHost, g.view === view), ratio ? m.maxCharsWithRatio : m.maxChars)
              const cls = `graph-node${n.status === 'Discovered' ? ' is-muted' : ''}${g.view.match ? ' is-match' : ''}`
                + `${hasKids ? ' has-kids' : ''}${n.pageId === selectedId ? ' is-selected' : ''}`
              const badgeW = Math.max(24, 10 + String(g.hidden).length * 6.5)
              return (
                <g
                  key={n.pageId}
                  data-pid={n.pageId}
                  className={cls}
                  transform={`translate(${g.x},${g.y})`}
                  tabIndex={0}
                  role="treeitem"
                  aria-expanded={hasKids ? g.open : undefined}
                  aria-selected={n.pageId === selectedId}
                  aria-label={`${n.url}, ${n.status}${hasRatio(n) ? `, ratio ${fmtPercent(n.domainLinkRatio)}` : ''}${g.hidden > 0 ? `, ${g.hidden} hidden children` : ''}`}
                  onClick={() => activate(g)}
                  onKeyDown={(e) => onKey(e, g)}
                  onMouseEnter={() => setHoverId(n.pageId)}
                  onMouseLeave={() => setHoverId((h) => (h === n.pageId ? null : h))}
                  onFocus={() => setHoverId(n.pageId)}
                  onBlur={() => setHoverId((h) => (h === n.pageId ? null : h))}
                >
                  <rect className="graph-box" width={m.nodeW} height={NODE_H} rx={NODE_H / 2} ry={NODE_H / 2} />
                  <circle cx={12} cy={NODE_H / 2} r={3.5} fill={STATUS_COLOR[n.status]} />
                  <text className="graph-label" x={21} y={NODE_H / 2} dominantBaseline="central">{text}</text>
                  {ratio && (
                    <text className="graph-ratio" x={m.nodeW - 11} y={NODE_H / 2} dominantBaseline="central" textAnchor="end">
                      {Math.round(n.domainLinkRatio * 100)}%
                    </text>
                  )}
                  {g.hidden > 0 && (
                    <g className="graph-badge" transform={`translate(${(m.nodeW - badgeW) / 2},${NODE_H - BADGE_H / 2})`}>
                      <rect width={badgeW} height={BADGE_H} rx={BADGE_H / 2} ry={BADGE_H / 2} />
                      <text x={badgeW / 2} y={BADGE_H / 2} dominantBaseline="central" textAnchor="middle">+{g.hidden}</text>
                    </g>
                  )}
                </g>
              )
            })}
          </svg>
          {tooltip}
        </div>
      </div>
      <div className="graph-detail" aria-live="polite">
        {sel ? (
          <>
            <div className="graph-detail-head">
              <ExternalLink className="mono graph-detail-url" url={sel.url} />
              <button type="button" className="btn-sm" onClick={() => setSelectedId(null)} aria-label="Clear selection">×</button>
            </div>
            {sel.title && <div className="tip-title">“{sel.title}”</div>}
            <div className="facts">
              <NodeFacts n={sel} />
              {sel.finalUrl && <span className="facts-item muted">→ redirected to <ExternalLink className="mono" url={sel.finalUrl} /></span>}
              {sel.error && <span className="facts-item node-error">{sel.error}</span>}
              {hasRatio(sel) && (
                <button
                  type="button"
                  className="btn-sm"
                  aria-expanded={linksFor === sel.pageId}
                  onClick={() => setLinksFor((p) => (p === sel.pageId ? null : sel.pageId))}
                >
                  {linksFor === sel.pageId ? 'Hide' : 'Show'} links ({sel.outgoingLinkCount})
                </button>
              )}
            </div>
            {linksFor === sel.pageId && <LinksPanel jobId={jobId} pageId={sel.pageId} />}
          </>
        ) : (
          <span className="muted">Click a node to expand/collapse it and see its details here. Hover for the full URL.</span>
        )}
      </div>
    </div>
  )
}
