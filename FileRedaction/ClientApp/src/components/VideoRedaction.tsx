import { useState, useRef, useCallback, useEffect } from 'react'

interface VideoStatusResponse {
  status: string
  phase: string
  errorMessage?: string
  downloadUrl?: string
  originalFileName?: string
}

interface Props {
  onBack: () => void
}

type Step = 'upload' | 'processing' | 'done'

const PHASE_LABELS: Record<string, string> = {
  uploading_to_sr: 'Uploading to SecureRedact…',
  detecting: 'Detecting faces, license plates, text and audio…',
  redacting: 'Applying redactions…',
  publishing: 'Preparing download link…',
  ready: 'Complete'
}

const PHASES = [
  { key: 'uploading_to_sr', label: 'Uploading' },
  { key: 'detecting',       label: 'Detecting PII' },
  { key: 'redacting',       label: 'Redacting' },
  { key: 'publishing',      label: 'Publishing' },
]

function phaseIndex(phase: string): number {
  const i = PHASES.findIndex(p => p.key === phase)
  return i >= 0 ? i : (phase === 'ready' ? PHASES.length : 0)
}

export default function VideoRedaction({ onBack }: Props) {
  const [step, setStep] = useState<Step>('upload')
  const [dragging, setDragging] = useState(false)
  const [sessionId, setSessionId] = useState<string>('')
  const [originalFileName, setOriginalFileName] = useState<string>('')
  const [phase, setPhase] = useState<string>('uploading_to_sr')
  const [errorMsg, setErrorMsg] = useState<string>('')
  const [downloadUrl, setDownloadUrl] = useState<string>('')
  const [uploadProgress, setUploadProgress] = useState<string>('')

  const fileInputRef = useRef<HTMLInputElement>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    return () => { if (pollRef.current) clearInterval(pollRef.current) }
  }, [])

  // Poll when processing
  useEffect(() => {
    if (step !== 'processing' || !sessionId) return

    pollRef.current = setInterval(async () => {
      try {
        const res = await fetch(`/api/video/${sessionId}/status`)
        if (!res.ok) return
        const data: VideoStatusResponse = await res.json()

        setPhase(data.phase ?? 'uploading_to_sr')

        if (data.status === 'ready' && data.downloadUrl) {
          clearInterval(pollRef.current!)
          setDownloadUrl(data.downloadUrl)
          setStep('done')
        } else if (data.status === 'error') {
          clearInterval(pollRef.current!)
          setErrorMsg(data.errorMessage ?? 'Processing failed. Please try again.')
          setStep('upload')
        }
      } catch {
        // transient network error — retry on next tick
      }
    }, 5000)

    return () => { if (pollRef.current) clearInterval(pollRef.current) }
  }, [step, sessionId])

  const handleFile = useCallback(async (file: File) => {
    setErrorMsg('')
    setUploadProgress('Uploading…')

    const formData = new FormData()
    formData.append('file', file)

    try {
      const res = await fetch('/api/video/upload', { method: 'POST', body: formData })
      if (!res.ok) {
        const text = await res.text()
        setErrorMsg(text || 'Upload failed.')
        setUploadProgress('')
        return
      }
      const data = await res.json()
      setSessionId(data.sessionId)
      setOriginalFileName(data.originalFileName ?? file.name)
      setPhase('uploading_to_sr')
      setUploadProgress('')
      setStep('processing')
    } catch {
      setErrorMsg('Network error during upload.')
      setUploadProgress('')
    }
  }, [])

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setDragging(false)
    const file = e.dataTransfer.files[0]
    if (file) handleFile(file)
  }, [handleFile])

  const handleFileInput = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) handleFile(file)
  }, [handleFile])

  const resetAll = () => {
    setStep('upload')
    setSessionId('')
    setOriginalFileName('')
    setPhase('uploading_to_sr')
    setErrorMsg('')
    setDownloadUrl('')
  }

  const currentPhaseIdx = phaseIndex(phase)

  return (
    <div style={{ maxWidth: 700, margin: '32px auto', padding: '0 16px' }}>

      {/* Upload step */}
      {step === 'upload' && (
        <div style={styles.card}>
          <h2 style={styles.heading}>Video PII Redaction</h2>
          <p style={{ color: '#555', marginBottom: 24, fontSize: 15 }}>
            Upload a video file. SecureRedact will automatically detect and blur faces, license
            plates, on-screen text and audio. You will receive a download link when complete.
          </p>

          <div
            style={{ ...styles.dropzone, ...(dragging ? styles.dropzoneActive : {}) }}
            onDragOver={e => { e.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={handleDrop}
            onClick={() => fileInputRef.current?.click()}
          >
            <svg width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="#4f7ef8" strokeWidth={1.5} style={{ marginBottom: 12 }}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M15 10l4.553-2.069A1 1 0 0121 8.87v6.259a1 1 0 01-1.447.894L15 14M3 8a2 2 0 012-2h10a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2V8z" />
            </svg>
            <div style={{ fontWeight: 600, fontSize: 16, color: '#333', marginBottom: 6 }}>
              Drag & drop a video file
            </div>
            <div style={{ color: '#888', fontSize: 13 }}>or click to browse</div>
            <div style={{ color: '#aaa', fontSize: 12, marginTop: 8 }}>
              MP4 · MOV · AVI · MKV · WebM · WMV — max 500 MB
            </div>
            {uploadProgress && (
              <div style={{ color: '#4f7ef8', fontSize: 13, marginTop: 10 }}>{uploadProgress}</div>
            )}
          </div>

          <input
            ref={fileInputRef}
            type="file"
            accept=".mp4,.mov,.avi,.mkv,.webm,.wmv,video/*"
            style={{ display: 'none' }}
            onChange={handleFileInput}
          />

          {errorMsg && (
            <div style={styles.errorBanner}>{errorMsg}</div>
          )}

          <button style={{ ...styles.linkBtn, marginTop: 20 }} onClick={onBack}>
            Back to Documents
          </button>
        </div>
      )}

      {/* Processing step */}
      {step === 'processing' && (
        <div style={{ textAlign: 'center' }}>
          <div style={styles.card}>
            <div style={styles.spinner} />
            <h2 style={{ fontSize: 20, fontWeight: 700, margin: '20px 0 8px' }}>
              Processing video…
            </h2>
            <p style={{ color: '#555', fontSize: 14, marginBottom: 4 }}>
              {PHASE_LABELS[phase] ?? 'Processing…'}
            </p>
            <p style={{ color: '#aaa', fontSize: 12, marginBottom: 28 }}>{originalFileName}</p>

            {/* Step progress indicator */}
            <div style={styles.stepProgress}>
              {PHASES.map((p, i) => {
                const isDone = i < currentPhaseIdx
                const isActive = i === currentPhaseIdx
                return (
                  <div key={p.key} style={styles.stepProgressItem}>
                    {i > 0 && (
                      <div style={{
                        ...styles.progressConnector,
                        background: isDone || isActive ? '#4f7ef8' : '#e0e0e0'
                      }} />
                    )}
                    <div style={{
                      ...styles.progressCircle,
                      ...(isDone || isActive ? styles.progressCircleActive : {}),
                      ...(isActive ? styles.progressCircleCurrent : {})
                    }}>
                      {isDone ? (
                        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="#fff" strokeWidth={2}>
                          <path d="M2 6l3 3 5-5" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                      ) : (
                        <span style={{ fontSize: 11, fontWeight: 700 }}>{i + 1}</span>
                      )}
                    </div>
                    <span style={{
                      ...styles.progressLabel,
                      ...(isDone || isActive ? styles.progressLabelActive : {})
                    }}>
                      {p.label}
                    </span>
                  </div>
                )
              })}
            </div>

            <p style={{ color: '#aaa', fontSize: 12, marginTop: 20 }}>
              Video processing can take several minutes. This page will update automatically.
            </p>
          </div>
        </div>
      )}

      {/* Done step */}
      {step === 'done' && (
        <div style={{ textAlign: 'center' }}>
          <div style={styles.card}>
            <div style={styles.doneIcon}>
              <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#16a34a" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </div>
            <h2 style={{ fontSize: 22, fontWeight: 700, marginBottom: 8 }}>Redaction Complete</h2>
            <p style={{ color: '#555', marginBottom: 8, fontSize: 15 }}>
              Your video has been processed by SecureRedact. Faces, license plates, on-screen text
              and audio have been automatically redacted.
            </p>
            <p style={{ color: '#aaa', fontSize: 13, marginBottom: 28 }}>{originalFileName}</p>

            {downloadUrl && (
              <a
                href={downloadUrl}
                target="_blank"
                rel="noopener noreferrer"
                style={styles.downloadBtn}
              >
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} style={{ marginRight: 8, flexShrink: 0 }}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                </svg>
                Download Redacted Video
              </a>
            )}

            <div style={{ display: 'flex', gap: 12, marginTop: 24, justifyContent: 'center' }}>
              <button style={styles.secondaryBtn} onClick={onBack}>
                Back to Documents
              </button>
              <button style={styles.primaryBtn} onClick={resetAll}>
                Redact Another Video
              </button>
            </div>
          </div>
        </div>
      )}

      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}

const styles: Record<string, React.CSSProperties> = {
  card: {
    background: '#fff',
    borderRadius: 16,
    padding: '32px 36px',
    boxShadow: '0 4px 24px rgba(0,0,0,0.08)'
  },
  heading: {
    fontSize: 22,
    fontWeight: 700,
    marginBottom: 8,
    color: '#1a1a2e'
  },
  dropzone: {
    border: '2px dashed #d0d7ff',
    borderRadius: 12,
    padding: '48px 32px',
    textAlign: 'center',
    cursor: 'pointer',
    transition: 'border-color 0.2s, background 0.2s',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center'
  },
  dropzoneActive: {
    borderColor: '#4f7ef8',
    background: '#f0f4ff'
  },
  errorBanner: {
    background: '#fef2f2',
    border: '1px solid #fca5a5',
    borderRadius: 8,
    color: '#dc2626',
    padding: '12px 16px',
    marginTop: 16,
    fontSize: 14
  },
  spinner: {
    width: 48,
    height: 48,
    border: '4px solid #e0e7ff',
    borderTop: '4px solid #4f7ef8',
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    margin: '0 auto'
  },
  stepProgress: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 0,
    marginTop: 8
  },
  stepProgressItem: {
    display: 'flex',
    alignItems: 'center',
    gap: 6
  },
  progressConnector: {
    width: 32,
    height: 2,
    borderRadius: 1
  },
  progressCircle: {
    width: 28,
    height: 28,
    borderRadius: '50%',
    background: '#e0e0e0',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#999',
    transition: 'all 0.2s'
  },
  progressCircleActive: {
    background: '#4f7ef8',
    color: '#fff'
  },
  progressCircleCurrent: {
    boxShadow: '0 0 0 3px #c7d9ff'
  },
  progressLabel: {
    fontSize: 12,
    color: '#aaa',
    whiteSpace: 'nowrap',
    marginRight: 4
  },
  progressLabelActive: {
    color: '#4f7ef8',
    fontWeight: 600
  },
  doneIcon: {
    width: 72,
    height: 72,
    borderRadius: '50%',
    background: '#dcfce7',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    margin: '0 auto 20px'
  },
  downloadBtn: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: '#4f7ef8',
    color: '#fff',
    padding: '14px 32px',
    borderRadius: 10,
    textDecoration: 'none',
    fontWeight: 700,
    fontSize: 16,
    boxShadow: '0 2px 12px rgba(79,126,248,0.35)'
  },
  primaryBtn: {
    background: '#4f7ef8',
    color: '#fff',
    border: 'none',
    borderRadius: 10,
    padding: '12px 24px',
    fontWeight: 600,
    fontSize: 15,
    cursor: 'pointer'
  },
  secondaryBtn: {
    background: '#f0f4ff',
    color: '#4f7ef8',
    border: '1px solid #c7d9ff',
    borderRadius: 10,
    padding: '12px 24px',
    fontWeight: 600,
    fontSize: 15,
    cursor: 'pointer'
  },
  linkBtn: {
    background: 'none',
    border: 'none',
    color: '#4f7ef8',
    cursor: 'pointer',
    fontSize: 14,
    padding: 0,
    textDecoration: 'underline'
  }
}
