import { useEffect, useMemo, useState } from 'react'
import { api, toError } from '../api/client'
import type { PageLinksResponse, TreeNodeDto } from '../api/types'
import type { ViewNode } from '../tree'
import { ExternalLink } from './ExternalLink'
import { RatioBar } from './RatioBar'
import { StatusBadge } from './StatusBadge'

interface Props {
  jobId: string
  view: ViewNode
  isOpen: (node: TreeNodeDto) => boolean
  onToggle: (node: TreeNodeDto, open: boolean) => void
}

export function LinksPanel({ jobId, pageId }: { jobId: string; pageId: number }) {
  const [links, setLinks] = useState<PageLinksResponse | null>(null)
  const [error, setError] = useState<Error | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const c = new AbortController()
    api.getPageLinks(jobId, pageId, c.signal).then(
      (r) => { if (!c.signal.aborted) setLinks(r) },
      (e: unknown) => { if (!c.signal.aborted) setError(toError(e)) },
    )
    return () => c.abort()
  }, [jobId, pageId, attempt])

  const counts = useMemo(() => {
    const internal = links ? links.links.filter((l) => l.isInternal).length : 0
    return { internal, external: links ? links.links.length - internal : 0 }
  }, [links])

  return (
    <div className="links-panel">
      {error && (
        <span className="node-error">
          {error.message}{' '}
          <button type="button" className="btn-sm" onClick={() => { setError(null); setAttempt((a) => a + 1) }}>Retry</button>
        </span>
      )}
      {!links && !error && <span className="muted">Loading links…</span>}
      {links && (
        <>
          <div className="muted links-summary">{counts.internal} internal · {counts.external} external</div>
          <ul className="links-list">
            {links.links.map((l) => (
              <li key={l.url}>
                <ExternalLink className="mono url" url={l.url} />
                <span className={`badge badge-sm ${l.isInternal ? 'badge-internal' : 'badge-external'}`}>
                  {l.isInternal ? 'internal' : 'external'}
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  )
}

export function TreeNode({ jobId, view, isOpen, onToggle }: Props) {
  const { node, children, match } = view
  const [linksOpen, setLinksOpen] = useState(false)
  const open = isOpen(node)
  const hasChildren = children.length > 0
  const crawled = node.status === 'Crawled'
  const notCrawled = node.status === 'Discovered'
  const canShowLinks = crawled && node.outgoingLinkCount > 0
  const total = node.children.length
  const http = node.httpStatusCode

  return (
    <li>
      <div className={`tree-row${notCrawled ? ' is-muted' : ''}${match ? ' is-match' : ''}`}>
        <div className="tree-main">
          {hasChildren ? (
            <button
              type="button"
              className="toggle"
              aria-expanded={open}
              aria-label={`${open ? 'Collapse' : 'Expand'} ${node.url}`}
              onClick={() => onToggle(node, !open)}
            >
              {open ? '▾' : '▸'}
            </button>
          ) : (
            <span className="toggle" aria-hidden="true">·</span>
          )}
          <ExternalLink className="mono url" url={node.url} />
          <StatusBadge status={node.status} />
          {http !== null && <span className={`http mono${http >= 400 ? ' http-bad' : ''}`}>HTTP {http}</span>}
        </div>
        <div className="tree-meta">
          {node.title && <span className="page-title" title={node.title}>“{node.title}”</span>}
          {crawled && <RatioBar ratio={node.domainLinkRatio} internal={node.internalLinkCount} total={node.outgoingLinkCount} />}
          {node.finalUrl && (
            <span className="muted redirect">→ redirected to <ExternalLink className="mono" url={node.finalUrl} /></span>
          )}
          {notCrawled && <span className="muted">not crawled (page budget / depth limit)</span>}
          {node.error && <span className="node-error">{node.error}</span>}
          {canShowLinks && (
            <button type="button" className="btn-sm" aria-expanded={linksOpen} onClick={() => setLinksOpen((o) => !o)}>
              {linksOpen ? 'Hide' : 'Show'} links ({node.outgoingLinkCount})
            </button>
          )}
          {total > 0 && (
            <span className="muted">
              {children.length < total ? `${children.length} of ${total} children shown` : `${total} ${total === 1 ? 'child' : 'children'}`}
            </span>
          )}
        </div>
      </div>
      {linksOpen && <LinksPanel jobId={jobId} pageId={node.pageId} />}
      {hasChildren && open && (
        <ul className="tree">
          {children.map((c) => (
            <TreeNode key={c.node.pageId} jobId={jobId} view={c} isOpen={isOpen} onToggle={onToggle} />
          ))}
        </ul>
      )}
    </li>
  )
}
