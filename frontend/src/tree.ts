import { PAGE_STATUSES, type PageStatus, type TreeNodeDto } from './api/types'

export interface TreeStats {
  byStatus: Record<PageStatus, number>
  /** Page count per depth; index = depth. */
  byDepth: number[]
  internalLinks: number
  externalLinks: number
  /** Mean of per-page domain-link ratios over crawled pages with at least one outgoing link. */
  avgRatio: number | null
  ratioPages: number
  /** Deepest level holding a fetched (non-Discovered) page; -1 when there is none. */
  maxFetchedDepth: number
}

function walk(node: TreeNodeDto, visit: (n: TreeNodeDto) => void): void {
  const stack = [node]
  while (stack.length > 0) {
    const n = stack.pop()!
    visit(n)
    for (const c of n.children) stack.push(c)
  }
}

export function computeStats(root: TreeNodeDto | null): TreeStats {
  const byStatus = Object.fromEntries(PAGE_STATUSES.map((s) => [s, 0])) as Record<PageStatus, number>
  const byDepth: number[] = []
  let internalLinks = 0
  let externalLinks = 0
  let ratioSum = 0
  let ratioPages = 0
  let maxFetchedDepth = -1
  if (root) {
    walk(root, (n) => {
      byStatus[n.status] += 1
      byDepth[n.depth] = (byDepth[n.depth] ?? 0) + 1
      internalLinks += n.internalLinkCount
      externalLinks += n.outgoingLinkCount - n.internalLinkCount
      if (n.status !== 'Discovered' && n.depth > maxFetchedDepth) maxFetchedDepth = n.depth
      if (n.status === 'Crawled' && n.outgoingLinkCount > 0) {
        ratioSum += n.domainLinkRatio
        ratioPages += 1
      }
    })
  }
  return {
    byStatus,
    byDepth: Array.from(byDepth, (c) => c ?? 0),
    internalLinks,
    externalLinks,
    avgRatio: ratioPages > 0 ? ratioSum / ratioPages : null,
    ratioPages,
    maxFetchedDepth,
  }
}

/** A node as displayed: children are the visible (filtered) subset; `match` marks direct filter hits. */
export interface ViewNode {
  node: TreeNodeDto
  children: ViewNode[]
  match: boolean
}

export interface TreeFilter {
  query: string
  statuses: ReadonlySet<PageStatus>
}

export const isFiltering = (f: TreeFilter): boolean =>
  f.query.trim() !== '' || f.statuses.size < PAGE_STATUSES.length

/**
 * Keeps nodes whose URL (or redirect target) contains the query (case-insensitive) and whose status is
 * selected, plus every ancestor of such a node so matches stay in context. Returns null if nothing matches.
 */
export function buildView(root: TreeNodeDto, filter: TreeFilter): ViewNode | null {
  const active = isFiltering(filter)
  const q = filter.query.trim().toLowerCase()
  const visit = (n: TreeNodeDto): ViewNode | null => {
    const children: ViewNode[] = []
    for (const c of n.children) {
      const v = visit(c)
      if (v) children.push(v)
    }
    if (!active) return { node: n, children, match: false }
    const textHit = q === '' || n.url.toLowerCase().includes(q) || (n.finalUrl?.toLowerCase().includes(q) ?? false)
    const match = textHit && filter.statuses.has(n.status)
    return match || children.length > 0 ? { node: n, children, match } : null
  }
  return visit(root)
}
