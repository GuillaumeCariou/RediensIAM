import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
/**
 * Fonts are self-hosted so the CSP needs no fonts.googleapis.com / fonts.gstatic.com allowance,
 * and so the login page renders without talking to a third party. Vite emits these as same-origin
 * assets. A Google Fonts <link> here would widen the CSP and hand a third party the Referer of
 * every sign-in — including one carrying a token in the URL.
 */
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './index.css'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
