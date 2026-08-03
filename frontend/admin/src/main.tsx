import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
/**
 * Fonts are self-hosted so the CSP needs no fonts.googleapis.com / fonts.gstatic.com allowance,
 * and so the console renders without talking to a third party. Vite emits these as same-origin
 * assets. Swapping them back to a Google Fonts <link> widens the CSP and leaks admin page loads.
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
