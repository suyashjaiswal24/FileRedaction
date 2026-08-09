import { useState, useEffect, useRef } from 'react'
import FileUpload from './components/FileUpload'
import EntitySelector from './components/EntitySelector'
import DocumentPreview from './components/DocumentPreview'
import AudioRedaction from './components/AudioRedaction'
import VideoRedaction from './components/VideoRedaction'
import { getUploadStatus } from './api'
import type { UploadResponse, UploadAcceptedResponse, PreviewResponse } from './types'

type Step = 'upload' | 'processing' | 'select' | 'preview' | 'done'
type Mode = 'document' | 'audio' | 'video'

const STEPS = ['Upload', 'Select PII', 'Preview', 'Redact']

const PHASE_LABELS: Record<string, string> = {
  extracting: 'Extracting text with Azure Document Intelligence…',
  detecting: 'Detecting PII entities…',
  '': 'Finalising…'
}

export default function App() {
  const [mode, setMode] = useState<Mode>('document')
  const [step, setStep] = useState<Step>('upload')
  const [uploadResult, setUploadResult] = useState<UploadResponse | null>(null)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [processingSession, setProcessingSession] = useState<{ sessionId: string; originalFileName: string } | null>(null)
  const [processingPhase, setProcessingPhase] = useState<string>('extracting')
  const [processingError, setProcessingError] = useState<string>('')
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const [previewCache, setPreviewCache] = useState<{ key: string; data: PreviewResponse } | null>(null)

  function handleEntityAdded(entity: import('./types').PiiEntity) {
    if (!uploadResult) return
    setUploadResult({ ...uploadResult, entities: [...uploadResult.entities, entity] })
    setPreviewCache(null) // new entity added — invalidate cached preview
  }

  function handleSelectionChange(ids: Set<string>) {
    setSelectedIds(ids)
    setPreviewCache(null) // selection changed — invalidate cached preview
  }

  const stepIndex: Record<Step, number> = { upload: 0, processing: 0, select: 1, preview: 2, done: 3 }

  function switchMode(m: Mode) {
    setMode(m)
    // Reset document state when switching away
    if (m === 'audio' || m === 'video') {
      setStep('upload')
      setUploadResult(null)
      setSelectedIds(new Set())
      setProcessingError('')
      if (pollRef.current) clearInterval(pollRef.current)
    }
  }

  function handleUploaded(result: UploadAcceptedResponse) {
    setProcessingSession({ sessionId: result.sessionId, originalFileName: result.originalFileName })
    setProcessingPhase('extracting')
    setProcessingError('')
    setStep('processing')
  }

  // Poll for background processing status
  useEffect(() => {
    if (step !== 'processing' || !processingSession) return

    pollRef.current = setInterval(async () => {
      try {
        const status = await getUploadStatus(processingSession.sessionId)
        setProcessingPhase(status.phase ?? '')

        if (status.status === 'ready' && status.entities) {
          clearInterval(pollRef.current!)
          const result: UploadResponse = {
            sessionId: processingSession.sessionId,
            originalFileName: processingSession.originalFileName,
            entities: status.entities
          }
          setUploadResult(result)
          setSelectedIds(new Set(status.entities.map(e => e.id)))
          setStep('select')
        } else if (status.status === 'error') {
          clearInterval(pollRef.current!)
          setProcessingError(status.errorMessage ?? 'Processing failed. Please try again.')
          setStep('upload')
        }
      } catch {
        // transient network error — will retry on next tick
      }
    }, 2000)

    return () => { if (pollRef.current) clearInterval(pollRef.current) }
  }, [step, processingSession])

  return (
    <div style={styles.root}>
      {/* Top bar */}
      <header style={styles.topbar}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 24 }}>
          <div style={styles.brand}>
            <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#4f7ef8" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
            <span style={styles.brandName}>File Redaction POC</span>
          </div>

          {/* Mode tabs */}
          <div style={{ display: 'flex', gap: 4, background: '#f0f0f0', borderRadius: 8, padding: 3 }}>
            <button
              onClick={() => switchMode('document')}
              style={{
                ...styles.modeTab,
                ...(mode === 'document' ? styles.modeTabActive : {})
              }}
            >
              Documents
            </button>
            <button
              onClick={() => switchMode('audio')}
              style={{
                ...styles.modeTab,
                ...(mode === 'audio' ? styles.modeTabActive : {})
              }}
            >
              Audio
            </button>
            <button
              onClick={() => switchMode('video')}
              style={{
                ...styles.modeTab,
                ...(mode === 'video' ? styles.modeTabActive : {})
              }}
            >
              Video
            </button>
          </div>
        </div>

        <nav style={styles.stepper}>
          {STEPS.map((label, i) => {
            const current = stepIndex[step]
            const active = i <= current
            const isCurrent = i === current
            return (
              <div key={label} style={styles.stepItem}>
                {i > 0 && <div style={{ ...styles.connector, background: active ? '#4f7ef8' : '#e0e0e0' }} />}
                <div style={{ ...styles.stepCircle, ...(active ? styles.stepCircleActive : {}), ...(isCurrent ? styles.stepCircleCurrent : {}) }}>
                  {i < current ? (
                    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="#fff" strokeWidth={2}>
                      <path d="M2 6l3 3 5-5" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  ) : (
                    <span style={{ fontSize: 11, fontWeight: 700 }}>{i + 1}</span>
                  )}
                </div>
                <span style={{ ...styles.stepLabel, ...(active ? styles.stepLabelActive : {}) }}>{label}</span>
              </div>
            )
          })}
        </nav>
      </header>

      {/* Content */}
      <main style={styles.main}>
        {mode === 'audio' && (
          <AudioRedaction onBack={() => switchMode('document')} />
        )}

        {mode === 'video' && (
          <VideoRedaction onBack={() => switchMode('document')} />
        )}

        {mode === 'document' && step === 'upload' && (
          <FileUpload onUploadComplete={handleUploaded} initialError={processingError} />
        )}

        {mode === 'document' && step === 'processing' && processingSession && (
          <div style={{ maxWidth: 480, margin: '80px auto', textAlign: 'center' }}>
            <div style={styles.doneCard}>
              <div style={styles.spinner} />
              <h2 style={{ fontSize: 20, fontWeight: 700, margin: '20px 0 8px' }}>Analysing document…</h2>
              <p style={{ color: '#555', fontSize: 14 }}>
                {PHASE_LABELS[processingPhase] ?? PHASE_LABELS['']}
              </p>
              <p style={{ color: '#aaa', fontSize: 12, marginTop: 8 }}>{processingSession.originalFileName}</p>
            </div>
          </div>
        )}

        {mode === 'document' && step === 'select' && uploadResult && (
          <EntitySelector
            sessionId={uploadResult.sessionId}
            entities={uploadResult.entities}
            selectedIds={selectedIds}
            onSelectionChange={handleSelectionChange}
            onEntityAdded={handleEntityAdded}
            onNext={() => setStep('preview')}
            fileName={uploadResult.originalFileName}
          />
        )}

        {mode === 'document' && step === 'preview' && uploadResult && (
          <DocumentPreview
            sessionId={uploadResult.sessionId}
            selectedEntityIds={[...selectedIds]}
            selectedCount={selectedIds.size}
            fileName={uploadResult.originalFileName}
            onBack={() => setStep('select')}
            onDone={() => setStep('done')}
            previewCache={previewCache}
            onPreviewCached={setPreviewCache}
          />
        )}

        {mode === 'document' && step === 'done' && (
          <div style={{ maxWidth: 480, margin: '80px auto', textAlign: 'center' }}>
            <div style={styles.doneCard}>
              <div style={styles.doneIcon}>
                <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#16a34a" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </div>
              <h2 style={{ fontSize: 22, fontWeight: 700, marginBottom: 8 }}>Redaction Complete</h2>
              <p style={{ color: '#555', marginBottom: 28, fontSize: 15 }}>
                Your redacted PDF has been downloaded. Selected PII regions have been permanently blacked out using Aspose.PDF.
              </p>
              <button
                style={styles.primaryBtn}
                onClick={() => { setStep('upload'); setUploadResult(null); setSelectedIds(new Set()) }}
              >
                Redact Another Document
              </button>
            </div>
          </div>
        )}
      </main>

      <style>{`
        @keyframes spin { to { transform: rotate(360deg); } }
      `}</style>
    </div>
  )
}

const styles: Record<string, React.CSSProperties> = {
  root: { minHeight: '100vh', display: 'flex', flexDirection: 'column' },
  topbar: {
    background: '#fff',
    boxShadow: '0 1px 4px rgba(0,0,0,0.08)',
    padding: '0 40px',
    height: 64,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    position: 'sticky',
    top: 0,
    zIndex: 10
  },
  brand: { display: 'flex', alignItems: 'center', gap: 10 },
  brandName: { fontWeight: 700, fontSize: 18, color: '#1a1a2e' },
  modeTab: {
    background: 'none', border: 'none', borderRadius: 6, padding: '5px 14px',
    fontSize: 13, fontWeight: 600, cursor: 'pointer', color: '#888', transition: 'all 0.15s'
  },
  modeTabActive: { background: '#fff', color: '#4f7ef8', boxShadow: '0 1px 4px rgba(0,0,0,0.1)' },
  stepper: { display: 'flex', alignItems: 'center', gap: 0 },
  stepItem: { display: 'flex', alignItems: 'center', gap: 6 },
  connector: { width: 32, height: 2, borderRadius: 1 },
  stepCircle: {
    width: 26, height: 26, borderRadius: '50%',
    background: '#e0e0e0', display: 'flex', alignItems: 'center', justifyContent: 'center',
    color: '#999', transition: 'all 0.2s'
  },
  stepCircleActive: { background: '#4f7ef8', color: '#fff' },
  stepCircleCurrent: { boxShadow: '0 0 0 3px #c7d9ff' },
  stepLabel: { fontSize: 13, color: '#aaa', marginRight: 4, whiteSpace: 'nowrap' },
  stepLabelActive: { color: '#4f7ef8', fontWeight: 600 },
  main: { flex: 1, padding: '0 16px 40px' },
  doneCard: {
    background: '#fff', borderRadius: 16, padding: '48px 40px',
    boxShadow: '0 4px 24px rgba(0,0,0,0.08)'
  },
  doneIcon: {
    width: 72, height: 72, borderRadius: '50%', background: '#dcfce7',
    display: 'flex', alignItems: 'center', justifyContent: 'center', margin: '0 auto 20px'
  },
  spinner: {
    width: 48, height: 48, border: '4px solid #e0e7ff', borderTop: '4px solid #4f7ef8',
    borderRadius: '50%', animation: 'spin 0.8s linear infinite', margin: '0 auto'
  },
  primaryBtn: {
    background: '#4f7ef8', color: '#fff', border: 'none', borderRadius: 10,
    padding: '12px 28px', fontWeight: 600, fontSize: 15, cursor: 'pointer'
  }
}
