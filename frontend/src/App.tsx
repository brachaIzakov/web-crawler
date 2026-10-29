import { Link, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { HistoryPage } from './pages/HistoryPage'
import { JobDetailsPage } from './pages/JobDetailsPage'
import { StartCrawlPage } from './pages/StartCrawlPage'

function PlayIcon() {
  return (
    <svg className="nav-icon" viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false">
      <circle cx="12" cy="12" r="9.5" fill="none" stroke="currentColor" strokeWidth="2" />
      <path d="M10 8.2v7.6l6-3.8z" fill="currentColor" />
    </svg>
  )
}

function ClockIcon() {
  return (
    <svg className="nav-icon" viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false">
      <circle cx="12" cy="12" r="9.5" fill="none" stroke="currentColor" strokeWidth="2" />
      <path d="M12 7v5l3.5 2" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function MainNav() {
  // Job details belong to the History section, so keep that tab highlighted there.
  const { pathname } = useLocation()
  const inJob = pathname.startsWith('/jobs/')
  const cls = (active: boolean) => `nav-tab${active ? ' active' : ''}`
  return (
    <header className="nav">
      <nav className="nav-tabs" aria-label="Main">
        <NavLink to="/" end className={({ isActive }) => cls(isActive)}>
          <PlayIcon /> Start Crawl
        </NavLink>
        <NavLink
          to="/history"
          className={({ isActive }) => cls(isActive || inJob)}
          aria-current={inJob ? 'page' : undefined}
        >
          <ClockIcon /> History
        </NavLink>
      </nav>
    </header>
  )
}

export default function App() {
  return (
    <>
      <MainNav />
      <main>
        <Routes>
          <Route path="/" element={<StartCrawlPage />} />
          <Route path="/jobs/:id" element={<JobDetailsPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="*" element={<div className="card"><h1>Page not found</h1><p><Link to="/">Start a crawl</Link></p></div>} />
        </Routes>
      </main>
    </>
  )
}
