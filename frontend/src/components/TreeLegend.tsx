import { Popover } from './Popover'
import { StatusBadge } from './StatusBadge'

/** Compact "Legend" toggle; the explanations live in a popover so they don't crowd the tree. */
export function TreeLegend() {
  return (
    <Popover label="? Legend" buttonClassName="btn-quiet" ariaLabel="Show legend">
      <dl className="legend-list">
        <dt><StatusBadge status="Crawled" /></dt><dd>Fetched and parsed as HTML; its links were extracted.</dd>
        <dt><StatusBadge status="Failed" /></dt><dd>Request failed (network error, timeout or HTTP error status).</dd>
        <dt><StatusBadge status="Skipped" /></dt><dd>Fetched, but not HTML (e.g. image, PDF), so no links were extracted.</dd>
        <dt><StatusBadge status="Discovered" /></dt><dd>Linked from a crawled page but never fetched: the page budget or max depth was reached, or the job stopped.</dd>
        <dt>Ratio</dt><dd><strong>Domain-link ratio</strong> = links to the starting domain ÷ all outgoing links on that page, e.g. 75.5% (80/106).</dd>
        <dt>→ redirected</dt><dd>The server redirected; content was fetched from the shown URL.</dd>
        <dt><span className="graph-badge-sample">+N</span></dt><dd>Graph: N children hidden. Click a node (or Enter/Space) to expand/collapse and show its details; hover for the full URL.</dd>
      </dl>
    </Popover>
  )
}
