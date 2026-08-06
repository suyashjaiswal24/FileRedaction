import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { registerBuiltinPreviewRenderers } from '@doc-preview/core'
import { registerOfficePreviewRenderers } from '@doc-preview/office'
import '@doc-preview/themes/doc-preview.css'
import './index.css'
import App from './App'

registerBuiltinPreviewRenderers()
registerOfficePreviewRenderers()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>
)
