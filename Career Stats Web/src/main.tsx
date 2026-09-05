import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles.css'

/**
 * Apply a saved theme BEFORE the first paint.
 *
 * The stylesheet follows the OS by default, so somebody who has explicitly chosen dark on
 * a light machine would otherwise get a white flash on every load while React mounts and
 * stamps the attribute. Reading one string synchronously here costs nothing and removes
 * it. React then owns the attribute for the rest of the session.
 */
try {
  const saved = window.localStorage.getItem('eet.theme')
  if (saved === 'light' || saved === 'dark') {
    document.documentElement.setAttribute('data-theme', saved)
  }
} catch {
  // No storage, no preference to restore. The OS setting is the default anyway.
}

const root = document.getElementById('root')
if (!root) throw new Error('#root is missing from index.html')

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
