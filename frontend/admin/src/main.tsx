import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Self-hosted so the CSP needs no fonts.googleapis.com / fonts.gstatic.com allowance, and so the
// console renders without talking to a third party. Vite emits these as same-origin assets.
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './index.css'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
