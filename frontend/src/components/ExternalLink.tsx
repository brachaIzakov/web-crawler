import { safeHref } from '../format'

interface Props { url: string; className?: string; children?: string }

/** Opens a crawled URL in a new tab; non-http(s) URLs are shown as plain text, never as links. */
export function ExternalLink({ url, className, children }: Props) {
  const href = safeHref(url)
  if (!href) return <span className={className} title={url}>{children ?? url}</span>
  return (
    <a className={className} href={href} title={url} target="_blank" rel="noopener noreferrer">{children ?? url}</a>
  )
}
