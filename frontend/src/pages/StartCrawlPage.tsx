import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, toError } from '../api/client'
import { ErrorMessage } from '../components/ErrorMessage'

const STORAGE_KEY = 'crawler:last-crawl'
const EXAMPLES = ['https://example.com/', 'https://github.com/', 'https://developer.mozilla.org/']
const DEPTH = { min: 1, max: 5, def: 2 }
const PAGES = { min: 1, max: 1000, def: 200 }

interface FormValues { url: string; maxDepth: string; maxPages: string }
type FieldErrors = Partial<Record<keyof FormValues, string>>

const DEFAULTS: FormValues = { url: '', maxDepth: String(DEPTH.def), maxPages: String(PAGES.def) }

function loadSaved(): FormValues {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return DEFAULTS
    const v: unknown = JSON.parse(raw)
    if (typeof v === 'object' && v !== null) {
      const o = v as Record<string, unknown>
      if (typeof o.url === 'string' && typeof o.maxDepth === 'string' && typeof o.maxPages === 'string') {
        return { url: o.url, maxDepth: o.maxDepth, maxPages: o.maxPages }
      }
    }
  } catch { /* storage unavailable or corrupt: use defaults */ }
  return DEFAULTS
}

function save(values: FormValues): void {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(values)) } catch { /* storage unavailable */ }
}

function validateUrl(raw: string): string | undefined {
  const v = raw.trim()
  if (!v) return 'Enter the URL to start crawling from.'
  let u: URL
  try { u = new URL(v) } catch {
    return /^[\w-]+(\.[\w-]+)+/.test(v)
      ? `Include the scheme, e.g. https://${v}`
      : 'This is not a valid URL. Example: https://example.com/'
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'Only http:// and https:// URLs can be crawled.'
  return undefined
}

function validateInt(raw: string, label: string, r: { min: number; max: number }): string | undefined {
  if (raw.trim() === '') return undefined // server default applies
  const n = Number(raw)
  if (!Number.isInteger(n)) return `${label} must be a whole number.`
  if (n < r.min || n > r.max) return `${label} must be between ${r.min} and ${r.max}.`
  return undefined
}

function validate(v: FormValues): FieldErrors {
  const e: FieldErrors = {
    url: validateUrl(v.url),
    maxDepth: validateInt(v.maxDepth, 'Max depth', DEPTH),
    maxPages: validateInt(v.maxPages, 'Max pages', PAGES),
  }
  return Object.fromEntries(Object.entries(e).filter(([, m]) => m !== undefined))
}

export function StartCrawlPage() {
  const nav = useNavigate()
  const [values, setValues] = useState<FormValues>(loadSaved)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [submitting, setSubmitting] = useState(false)
  const [apiError, setApiError] = useState<Error | null>(null)

  const set = (field: keyof FormValues, value: string) => {
    setValues((v) => ({ ...v, [field]: value }))
    setErrors((e) => ({ ...e, [field]: undefined }))
  }

  const onSubmit = async (ev: FormEvent) => {
    ev.preventDefault()
    setApiError(null)
    const e = validate(values)
    setErrors(e)
    if (Object.keys(e).length > 0) return
    setSubmitting(true)
    try {
      const { jobId } = await api.createJob({
        url: values.url.trim(),
        ...(values.maxDepth.trim() !== '' ? { maxDepth: Number(values.maxDepth) } : {}),
        ...(values.maxPages.trim() !== '' ? { maxPages: Number(values.maxPages) } : {}),
      })
      save({ ...values, url: values.url.trim() })
      nav(`/jobs/${jobId}`)
    } catch (err) {
      setApiError(toError(err))
      setSubmitting(false)
    }
  }

  return (
    <div className="card narrow">
      <p className="eyebrow">Web Crawler</p>
      <h1>Start a crawl</h1>
      <p className="intro">
        The crawler fetches the URL, extracts its links and follows them breadth-first up to the max depth,
        stopping after the max number of pages. Each page's domain-link ratio (share of links that stay on
        the starting domain) is recorded, and you can follow progress live and browse the resulting page tree.
      </p>
      <form onSubmit={onSubmit} noValidate>
        <div className="field">
          <label htmlFor="url">URL</label>
          <input id="url" className="mono" type="url" inputMode="url" autoComplete="url" value={values.url}
            placeholder="https://example.com/" onChange={(e) => set('url', e.target.value)}
            aria-invalid={!!errors.url} aria-describedby={errors.url ? 'url-error url-help' : 'url-help'} />
          {errors.url && <span id="url-error" className="field-error">{errors.url}</span>}
          <span id="url-help" className="help examples">
            Try:{' '}
            {EXAMPLES.map((u) => (
              <button key={u} type="button" className="chip" onClick={() => set('url', u)}>{u}</button>
            ))}
          </span>
        </div>
        <div className="row">
          <div className="field">
            <label htmlFor="maxDepth">Max depth</label>
            <input id="maxDepth" type="number" inputMode="numeric" min={DEPTH.min} max={DEPTH.max} value={values.maxDepth}
              onChange={(e) => set('maxDepth', e.target.value)} aria-invalid={!!errors.maxDepth}
              aria-describedby="maxDepth-help" />
            <span id="maxDepth-help" className="help">
              {DEPTH.min}–{DEPTH.max} link hops from the start page (default {DEPTH.def}).
            </span>
            {errors.maxDepth && <span className="field-error">{errors.maxDepth}</span>}
          </div>
          <div className="field">
            <label htmlFor="maxPages">Max pages</label>
            <input id="maxPages" type="number" inputMode="numeric" min={PAGES.min} max={PAGES.max} value={values.maxPages}
              onChange={(e) => set('maxPages', e.target.value)} aria-invalid={!!errors.maxPages}
              aria-describedby="maxPages-help" />
            <span id="maxPages-help" className="help">
              {PAGES.min}–{PAGES.max} pages fetched in total (default {PAGES.def}).
            </span>
            {errors.maxPages && <span className="field-error">{errors.maxPages}</span>}
          </div>
        </div>
        {apiError && <ErrorMessage title="Couldn't start the crawl" error={apiError} />}
        <div className="form-actions">
          <button type="submit" className="primary" disabled={submitting}>
            {submitting ? 'Starting…' : 'Start crawl'}
          </button>
          <button type="button" disabled={submitting} onClick={() => { setValues(DEFAULTS); setErrors({}) }}>Reset</button>
        </div>
      </form>
    </div>
  )
}
