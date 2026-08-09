import { useEffect, useState } from 'react'
import { DocPreview } from '@doc-preview/react'
import { getPreview, downloadRedacted } from '../api'
import type { PreviewResponse } from '../types'

interface Props {
  sessionId: string
  selectedEntityIds: string[]
  selectedCount: number
  fileName: string
  onBack: () => void
  onDone: () => void
  previewCache: { key: string; data: PreviewResponse } | null
  onPreviewCached: (cache: { key: string; data: PreviewResponse }) => void
  isEmail?: boolean
  attachmentCount?: number
}

const ENTERPRISE_KEY = 'DP-ENT-DEMO-2026-FULL'

export default function DocumentPreview({ sessionId, selectedEntityIds, selectedCount, fileName, onBack, onDone, previewCache, onPreviewCached, isEmail, attachmentCount }: Props) {
  const [status, setStatus] = useState<'loading' | 'ready' | 'redacting' | 'error'>('loading')
  const [preview, setPreview] = useState<PreviewResponse | null>(null)
  const [errorMsg, setErrorMsg] = useState('')

  const ext = fileName.split('.').pop()?.toLowerCase() ?? ''
  const cacheKey = [...selectedEntityIds].sort().join(',')

  useEffect(() => {
    if (previewCache?.key === cacheKey) {
      setPreview(previewCache.data)
      setStatus('ready')
      return
    }
    setStatus('loading')
    getPreview(sessionId, selectedEntityIds)
      .then(p => {
        setPreview(p)
        setStatus('ready')
        onPreviewCached({ key: cacheKey, data: p })
      })
      .catch(async err => { setErrorMsg(await errorMessage(err)); setStatus('error') })
  }, [cacheKey]) // eslint-disable-line react-hooks/exhaustive-deps

  async function handleRedact() {
    if (!preview) return
    setStatus('redacting')
    try {
      const blob = await downloadRedacted(sessionId, selectedEntityIds)
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      // Email sessions download a zip; Excel preview is HTML but downloads the original format
      const downloadExt = isEmail ? 'zip' : (previewFileType === 'html' ? ext : previewFileType)
      a.download = fileName.replace(/\.[^.]+$/, '') + '_redacted.' + downloadExt
      a.click()
      URL.revokeObjectURL(url)
      onDone()
    } catch (err) {
      setErrorMsg(await errorMessage(err))
      setStatus('error')
    }
  }

  async function errorMessage(err: unknown): Promise<string> {
    if (err && typeof err === 'object' && 'response' in err) {
      const r = (err as { response?: { data?: unknown } }).response
      if (r?.data) {
        if (r.data instanceof Blob) {
          try { return await (r.data as Blob).text() } catch { return 'Server error during redaction.' }
        }
        return typeof r.data === 'string' ? r.data : JSON.stringify(r.data)
      }
    }
    return err instanceof Error ? err.message : 'Unknown error'
  }

  // Use the actual file type from backend (Office files are converted to PDF server-side)
  const previewFileType = preview?.fileType ?? ext
  const previewFileName = preview
    ? fileName.replace(/\.[^.]+$/, '.' + preview.fileType)
    : fileName
  const isOfficeOnly = false // All office formats are converted to PDF before preview
  const documents = preview ? [{ uri: preview.fileUrl, fileName: previewFileName }] : []

  const subtitle = preview?.hasHighlights
    ? `${selectedCount} entit${selectedCount === 1 ? 'y' : 'ies'} highlighted in yellow — confirm to permanently black them out.`
    : `${selectedCount} entit${selectedCount === 1 ? 'y' : 'ies'} selected for redaction. Previewing ${previewFileType.toUpperCase()} document.`

  return (
    <div style={{ maxWidth: 960, margin: '40px auto' }}>
      <div style={styles.card}>

        {/* Header */}
        <div style={styles.header}>
          <div>
            <h2 style={styles.title}>
              {preview?.hasHighlights ? 'Preview Highlights' : 'Preview Document'}
            </h2>
            <p style={styles.subtitle}>{subtitle}</p>
          </div>
        </div>

        {/* Non-PDF info banner */}
        {status === 'ready' && preview && !preview.hasHighlights && (
          <div style={styles.infoBanner}>
            <svg width="16" height="16" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span>
              <strong>{preview.fileType.toUpperCase()}</strong> — yellow highlights are PDF-only.
              The selected text will still be permanently redacted when you confirm.
            </span>
          </div>
        )}

        {/* Email session banner */}
        {isEmail && (
          <div style={{ ...styles.infoBanner, background: '#f0fdf4', borderColor: '#bbf7d0', color: '#166534' }}>
            <svg width="16" height="16" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
            </svg>
            <span>
              Previewing <strong>email body</strong>.
              {(attachmentCount ?? 0) > 0
                ? ` ${attachmentCount} attachment${attachmentCount === 1 ? '' : 's'} will also be redacted and included in the ZIP download.`
                : ' No attachments detected.'}
            </span>
          </div>
        )}

        {/* Preview area */}
        <div style={styles.previewArea}>
          {status === 'loading' && (
            <div style={styles.centered}>
              <div style={styles.spinner} />
              <p style={{ marginTop: 16, color: '#555' }}>Generating highlighted preview…</p>
            </div>
          )}

          {status === 'redacting' && (
            <div style={styles.centered}>
              <div style={styles.spinner} />
              <p style={{ marginTop: 16, color: '#555' }}>Applying permanent redactions…</p>
            </div>
          )}

          {status === 'error' && (
            <div style={styles.centered}>
              <div style={styles.errorBox}><strong>Error:</strong> {errorMsg}</div>
            </div>
          )}

          {status === 'ready' && preview && previewFileType === 'html' && (
            <div style={{ width: '100%' }}>
              <div style={styles.previewToolbar}>
                <a href={preview.fileUrl} target="_blank" rel="noreferrer" style={styles.openTabLink}>
                  Open in new tab ↗
                </a>
              </div>
              <iframe
                src={preview.fileUrl}
                style={{ display: 'block', width: '100%', height: 600, border: 'none' }}
                title="Document preview"
              />
            </div>
          )}

          {status === 'ready' && preview && previewFileType !== 'html' && (
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              <div style={styles.previewToolbar}>
                <a href={preview.fileUrl} target="_blank" rel="noreferrer" style={styles.openTabLink}>
                  Open in new tab ↗
                </a>
              </div>
              <DocPreview
                documents={documents}
                enterpriseLicenseKey={ENTERPRISE_KEY}
                config={{
                  enterprise: {
                    enableAnnotations: true,
                    enableSearch: true,
                    enablePrint: true,
                  },
                }}
                style={{ width: '100%', height: 580 }}
              />
            </div>
          )}
        </div>

        {/* Footer */}
        <div style={styles.footer}>
          <button style={styles.backBtn} onClick={onBack} disabled={status === 'redacting'}>
            ← Back to Selection
          </button>

          {status === 'ready' && (
            isOfficeOnly
              ? (
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <span style={{ fontSize: 13, color: '#92400e', background: '#fef3c7', padding: '6px 12px', borderRadius: 8 }}>
                    {preview?.fileType.toUpperCase()} redaction not yet supported — convert to PDF first
                  </span>
                </div>
              ) : (
                <button style={styles.redactBtn} onClick={handleRedact}>
                  {isEmail
                    ? `Confirm & Redact (Download ZIP${(attachmentCount ?? 0) > 0 ? ` · ${(attachmentCount ?? 0) + 1} files` : ''})`
                    : 'Confirm & Redact Document'}
                </button>
              )
          )}
        </div>
      </div>
    </div>
  )
}

const styles: Record<string, React.CSSProperties> = {
  card: { background: '#fff', borderRadius: 16, padding: '32px 36px', boxShadow: '0 4px 24px rgba(0,0,0,0.08)' },
  header: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 16 },
  title: { fontSize: 22, fontWeight: 700 },
  subtitle: { color: '#666', fontSize: 14, marginTop: 4, maxWidth: 560 },
  infoBanner: {
    display: 'flex', alignItems: 'flex-start', gap: 8,
    padding: '10px 14px', marginBottom: 14,
    background: '#eff6ff', border: '1px solid #bfdbfe', borderRadius: 8,
    fontSize: 13, color: '#1e40af'
  },
  previewArea: {
    border: '1px solid #e5e7eb', borderRadius: 10,
    minHeight: 480, overflow: 'visible', background: '#f8f9fa',
    display: 'flex', flexDirection: 'column'
  },
  centered: { flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: 40 },
  spinner: {
    width: 44, height: 44,
    border: '4px solid #e0e7ff', borderTop: '4px solid #4f7ef8',
    borderRadius: '50%', animation: 'spin 0.8s linear infinite'
  },
  previewToolbar: {
    display: 'flex', justifyContent: 'flex-end', alignItems: 'center',
    padding: '6px 12px', background: '#f0f4ff', borderBottom: '1px solid #dbe4ff', flexShrink: 0
  },
  openTabLink: {
    fontSize: 12, color: '#4f7ef8', textDecoration: 'none', fontWeight: 500
  },
  openTabBtn: {
    background: '#4f7ef8', color: '#fff', borderRadius: 10,
    padding: '12px 28px', fontWeight: 600, fontSize: 15, textDecoration: 'none',
    display: 'inline-block'
  },
  errorBox: {
    padding: '12px 16px', background: '#fff0f0', border: '1px solid #fca5a5',
    borderRadius: 8, fontSize: 14, color: '#b91c1c', maxWidth: 480, textAlign: 'center'
  },
  footer: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 20, paddingTop: 20, borderTop: '1px solid #eee' },
  backBtn: {
    background: 'none', border: '1px solid #ddd', borderRadius: 10,
    padding: '10px 20px', fontWeight: 500, fontSize: 14, color: '#555', cursor: 'pointer'
  },
  redactBtn: {
    background: '#dc2626', color: '#fff', border: 'none', borderRadius: 10,
    padding: '10px 24px', fontWeight: 600, fontSize: 15, cursor: 'pointer'
  }
}
