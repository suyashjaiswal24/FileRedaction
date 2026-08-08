import { useState, useRef, useCallback, useEffect } from 'react'

interface AudioPiiEntity {
  id: string
  text: string
  category: string
  confidenceScore: number
  timeRanges: { startTicks: number; endTicks: number }[]
}

interface AudioStatusResponse {
  status: string
  errorMessage?: string
  transcript?: string
  entities?: AudioPiiEntity[]
  originalFileName?: string
}

interface Props {
  onBack: () => void
}

type Step = 'upload' | 'processing' | 'review' | 'done'

const CATEGORY_COLORS: Record<string, string> = {
  Person: '#4f7ef8',
  Email: '#f87c4f',
  PhoneNumber: '#a34ff8',
  Address: '#f84f8b',
  Organization: '#4fb8f8',
  DateTime: '#f8c84f',
  URL: '#4ff89b',
  IPAddress: '#f84f4f',
  Default: '#888'
}

function getCategoryColor(cat: string): string {
  return CATEGORY_COLORS[cat] ?? CATEGORY_COLORS.Default
}

function buildHighlightedTranscript(
  transcript: string,
  entities: AudioPiiEntity[],
  selectedIds: Set<string>
): React.ReactNode[] {
  // Build a list of [start, end, entityId] spans to highlight
  const spans: { start: number; end: number; entity: AudioPiiEntity }[] = []

  for (const entity of entities) {
    if (!selectedIds.has(entity.id)) continue
    let searchFrom = 0
    while (true) {
      const pos = transcript.toLowerCase().indexOf(entity.text.toLowerCase(), searchFrom)
      if (pos < 0) break
      spans.push({ start: pos, end: pos + entity.text.length, entity })
      searchFrom = pos + 1
    }
  }

  if (spans.length === 0) return [<span key="all">{transcript}</span>]

  // Sort by start, resolve overlaps (keep first)
  spans.sort((a, b) => a.start - b.start)
  const merged: typeof spans = []
  for (const s of spans) {
    if (merged.length > 0 && s.start < merged[merged.length - 1].end) continue
    merged.push(s)
  }

  const nodes: React.ReactNode[] = []
  let cursor = 0
  for (const s of merged) {
    if (cursor < s.start) {
      nodes.push(<span key={`plain-${cursor}`}>{transcript.slice(cursor, s.start)}</span>)
    }
    nodes.push(
      <mark
        key={`hl-${s.start}`}
        title={`${s.entity.category} (${Math.round(s.entity.confidenceScore * 100)}%)`}
        style={{
          background: '#fff176',
          borderRadius: 3,
          padding: '1px 2px',
          cursor: 'default',
          border: `1px solid ${getCategoryColor(s.entity.category)}44`
        }}
      >
        {transcript.slice(s.start, s.end)}
      </mark>
    )
    cursor = s.end
  }
  if (cursor < transcript.length) {
    nodes.push(<span key={`plain-end`}>{transcript.slice(cursor)}</span>)
  }
  return nodes
}

const LANGUAGES = [
  { code: 'en-US', label: 'English' },
  { code: 'de-DE', label: 'German' },
  { code: 'fr-FR', label: 'French' },
  { code: 'es-ES', label: 'Spanish' },
  { code: 'it-IT', label: 'Italian' },
  { code: 'nl-NL', label: 'Dutch' },
  { code: 'pt-BR', label: 'Portuguese' },
  { code: 'pl-PL', label: 'Polish' },
  { code: 'ru-RU', label: 'Russian' },
  { code: 'cs-CZ', label: 'Czech' },
  { code: 'da-DK', label: 'Danish' },
  { code: 'fi-FI', label: 'Finnish' },
  { code: 'nb-NO', label: 'Norwegian' },
  { code: 'sv-SE', label: 'Swedish' },
  { code: 'tr-TR', label: 'Turkish' },
  { code: 'ja-JP', label: 'Japanese' },
  { code: 'zh-CN', label: 'Chinese (Simplified)' },
  { code: 'ko-KR', label: 'Korean' },
]

export default function AudioRedaction({ onBack }: Props) {
  const [step, setStep] = useState<Step>('upload')
  const [dragging, setDragging] = useState(false)
  const [language, setLanguage] = useState('en-US')
  const [sessionId, setSessionId] = useState<string>('')
  const [originalFileName, setOriginalFileName] = useState<string>('')
  const [transcript, setTranscript] = useState<string>('')
  const [entities, setEntities] = useState<AudioPiiEntity[]>([])
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [errorMsg, setErrorMsg] = useState<string>('')
  const [redactedUrl, setRedactedUrl] = useState<string>('')
  const [redactedFileName, setRedactedFileName] = useState<string>('')
  const [isRedacting, setIsRedacting] = useState(false)
  const [uploadProgress, setUploadProgress] = useState<string>('')

  const fileInputRef = useRef<HTMLInputElement>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const prevRedactedUrl = useRef<string>('')

  // Cleanup blob URLs on unmount
  useEffect(() => {
    return () => {
      if (prevRedactedUrl.current) URL.revokeObjectURL(prevRedactedUrl.current)
      if (pollRef.current) clearInterval(pollRef.current)
    }
  }, [])

  // Poll when processing
  useEffect(() => {
    if (step !== 'processing' || !sessionId) return

    pollRef.current = setInterval(async () => {
      try {
        const res = await fetch(`/api/audio/${sessionId}/status`)
        if (!res.ok) return
        const data: AudioStatusResponse = await res.json()

        if (data.status === 'ready' && data.transcript && data.entities) {
          clearInterval(pollRef.current!)
          setTranscript(data.transcript)
          setEntities(data.entities)
          setSelectedIds(new Set(data.entities.map(e => e.id)))
          setStep('review')
        } else if (data.status === 'error') {
          clearInterval(pollRef.current!)
          setErrorMsg(data.errorMessage ?? 'Processing failed.')
          setStep('upload')
        }
      } catch {
        // transient — retry on next tick
      }
    }, 2000)

    return () => { if (pollRef.current) clearInterval(pollRef.current) }
  }, [step, sessionId])

  const handleFile = useCallback(async (file: File) => {
    setErrorMsg('')
    setUploadProgress('Uploading…')

    const formData = new FormData()
    formData.append('file', file)
    formData.append('language', language)

    try {
      const res = await fetch('/api/audio/upload', { method: 'POST', body: formData })
      if (!res.ok) {
        const text = await res.text()
        setErrorMsg(text || 'Upload failed.')
        setUploadProgress('')
        return
      }
      const data = await res.json()
      setSessionId(data.sessionId)
      setOriginalFileName(data.originalFileName ?? file.name)
      setUploadProgress('')
      setStep('processing')
    } catch (err) {
      setErrorMsg('Network error during upload.')
      setUploadProgress('')
    }
  }, [language])

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

  const toggleEntity = (id: string) => {
    setSelectedIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const handleRedact = async () => {
    if (selectedIds.size === 0) return
    setIsRedacting(true)
    try {
      const res = await fetch('/api/audio/redact', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sessionId, selectedEntityIds: [...selectedIds] })
      })
      if (!res.ok) {
        const text = await res.text()
        setErrorMsg(text || 'Redaction failed.')
        setIsRedacting(false)
        return
      }
      const blob = await res.blob()
      if (prevRedactedUrl.current) URL.revokeObjectURL(prevRedactedUrl.current)
      const url = URL.createObjectURL(blob)
      prevRedactedUrl.current = url
      setRedactedUrl(url)
      setRedactedFileName(originalFileName.replace(/\.[^.]+$/, '') + '_redacted.wav')
      setStep('done')
    } catch {
      setErrorMsg('Network error during redaction.')
    } finally {
      setIsRedacting(false)
    }
  }

  const resetAll = () => {
    if (prevRedactedUrl.current) URL.revokeObjectURL(prevRedactedUrl.current)
    prevRedactedUrl.current = ''
    setRedactedUrl('')
    setStep('upload')
    setSessionId('')
    setTranscript('')
    setEntities([])
    setSelectedIds(new Set())
    setErrorMsg('')
  }

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <div style={{ maxWidth: 1100, margin: '32px auto', padding: '0 16px' }}>

      {/* Upload step */}
      {step === 'upload' && (
        <div style={styles.card}>
          <h2 style={styles.heading}>Audio PII Redaction</h2>
          <p style={{ color: '#555', marginBottom: 24, fontSize: 15 }}>
            Upload an audio file. Azure Speech-to-Text will transcribe it, PII will be detected,
            and selected segments will be replaced with a 1 kHz beep.
          </p>

          <div style={{ marginBottom: 20 }}>
            <label style={{ display: 'block', fontSize: 13, fontWeight: 600, color: '#444', marginBottom: 6 }}>
              Audio language
            </label>
            <select
              value={language}
              onChange={e => setLanguage(e.target.value)}
              style={styles.select}
            >
              {LANGUAGES.map(l => (
                <option key={l.code} value={l.code}>{l.label} ({l.code})</option>
              ))}
            </select>
          </div>

          <div
            style={{
              ...styles.dropzone,
              ...(dragging ? styles.dropzoneActive : {})
            }}
            onDragOver={e => { e.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={handleDrop}
            onClick={() => fileInputRef.current?.click()}
          >
            <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#4f7ef8" strokeWidth={1.5} style={{ marginBottom: 12 }}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3" />
            </svg>
            <div style={{ fontWeight: 600, fontSize: 16, color: '#333', marginBottom: 6 }}>
              Drag & drop an audio file
            </div>
            <div style={{ color: '#888', fontSize: 13 }}>or click to browse</div>
            <div style={{ color: '#aaa', fontSize: 12, marginTop: 8 }}>
              WAV · MP3 · M4A · AAC · OGG · FLAC · WMA — max 100 MB
            </div>
            {uploadProgress && (
              <div style={{ color: '#4f7ef8', fontSize: 13, marginTop: 10 }}>{uploadProgress}</div>
            )}
          </div>

          <input
            ref={fileInputRef}
            type="file"
            accept=".wav,.mp3,.m4a,.aac,.ogg,.flac,.wma,audio/*"
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
        <div style={{ maxWidth: 480, margin: '80px auto', textAlign: 'center' }}>
          <div style={styles.card}>
            <div style={styles.spinner} />
            <h2 style={{ fontSize: 20, fontWeight: 700, margin: '20px 0 8px' }}>Analysing audio…</h2>
            <p style={{ color: '#555', fontSize: 14 }}>
              Transcribing with Azure Speech-to-Text and detecting PII entities…
            </p>
            <p style={{ color: '#aaa', fontSize: 12, marginTop: 8 }}>{originalFileName}</p>
          </div>
        </div>
      )}

      {/* Review step */}
      {step === 'review' && (
        <div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 20 }}>
            <button style={styles.linkBtn} onClick={resetAll}>Upload another file</button>
            <span style={{ color: '#aaa', fontSize: 13 }}>{originalFileName}</span>
          </div>

          {errorMsg && <div style={styles.errorBanner}>{errorMsg}</div>}

          <div style={{ display: 'flex', gap: 20, alignItems: 'flex-start' }}>
            {/* Transcript panel (60%) */}
            <div style={{ ...styles.card, flex: '0 0 60%', minWidth: 0 }}>
              <h3 style={styles.panelHeading}>Transcript</h3>
              <div style={styles.transcriptBox}>
                <p style={{ lineHeight: 1.8, fontSize: 15, margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
                  {buildHighlightedTranscript(transcript, entities, selectedIds)}
                </p>
              </div>
              {transcript.length === 0 && (
                <p style={{ color: '#aaa', fontSize: 14, textAlign: 'center', padding: '24px 0' }}>
                  No transcript available.
                </p>
              )}
            </div>

            {/* Entities panel (40%) */}
            <div style={{ ...styles.card, flex: '0 0 calc(40% - 20px)', minWidth: 0 }}>
              <h3 style={styles.panelHeading}>
                Detected PII
                <span style={styles.badge}>{entities.length}</span>
              </h3>

              {entities.length === 0 ? (
                <p style={{ color: '#888', fontSize: 14, textAlign: 'center', padding: '24px 0' }}>
                  No PII detected in transcript.
                </p>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  {entities.map(entity => (
                    <label
                      key={entity.id}
                      style={{
                        ...styles.entityRow,
                        opacity: selectedIds.has(entity.id) ? 1 : 0.5
                      }}
                    >
                      <input
                        type="checkbox"
                        checked={selectedIds.has(entity.id)}
                        onChange={() => toggleEntity(entity.id)}
                        style={{ marginRight: 10, accentColor: '#4f7ef8', width: 16, height: 16, flexShrink: 0 }}
                      />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                          <span
                            style={{
                              ...styles.categoryBadge,
                              background: getCategoryColor(entity.category) + '22',
                              color: getCategoryColor(entity.category),
                              borderColor: getCategoryColor(entity.category) + '55'
                            }}
                          >
                            {entity.category}
                          </span>
                          <span style={{ fontWeight: 600, fontSize: 14, wordBreak: 'break-word' }}>
                            {entity.text}
                          </span>
                        </div>
                        <div style={{ color: '#aaa', fontSize: 12, marginTop: 3 }}>
                          {Math.round(entity.confidenceScore * 100)}% confidence
                          {entity.timeRanges.length > 0 && ` · ${entity.timeRanges.length} occurrence${entity.timeRanges.length > 1 ? 's' : ''}`}
                          {entity.timeRanges.length === 0 && ' · no audio timestamps found'}
                        </div>
                      </div>
                    </label>
                  ))}
                </div>
              )}

              <div style={{ marginTop: 24, display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, color: '#888' }}>
                  <span>{selectedIds.size} of {entities.length} entities selected</span>
                  <div style={{ display: 'flex', gap: 12 }}>
                    <button style={styles.textBtn} onClick={() => setSelectedIds(new Set(entities.map(e => e.id)))}>All</button>
                    <button style={styles.textBtn} onClick={() => setSelectedIds(new Set())}>None</button>
                  </div>
                </div>
                <button
                  style={{
                    ...styles.primaryBtn,
                    opacity: (selectedIds.size === 0 || isRedacting) ? 0.6 : 1,
                    cursor: (selectedIds.size === 0 || isRedacting) ? 'not-allowed' : 'pointer'
                  }}
                  onClick={handleRedact}
                  disabled={selectedIds.size === 0 || isRedacting}
                >
                  {isRedacting ? 'Redacting…' : `Redact & Download (${selectedIds.size} entity${selectedIds.size !== 1 ? 'ies' : 'y'})`}
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Done step */}
      {step === 'done' && (
        <div style={{ maxWidth: 600, margin: '40px auto' }}>
          <div style={styles.card}>
            <div style={styles.doneIcon}>
              <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#16a34a" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </div>
            <h2 style={{ fontSize: 22, fontWeight: 700, marginBottom: 8, textAlign: 'center' }}>
              Redaction Complete
            </h2>
            <p style={{ color: '#555', marginBottom: 24, fontSize: 15, textAlign: 'center' }}>
              PII segments have been replaced with a 1 kHz beep. Play the redacted audio below or download it.
            </p>

            {redactedUrl && (
              <div style={styles.audioContainer}>
                <audio
                  controls
                  src={redactedUrl}
                  style={{ width: '100%', borderRadius: 8 }}
                />
                <a
                  href={redactedUrl}
                  download={redactedFileName}
                  style={styles.downloadLink}
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} style={{ marginRight: 6 }}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  Download {redactedFileName}
                </a>
              </div>
            )}

            <div style={{ display: 'flex', gap: 12, marginTop: 24, justifyContent: 'center' }}>
              <button style={styles.secondaryBtn} onClick={() => setStep('review')}>
                Adjust Selection
              </button>
              <button style={styles.primaryBtn} onClick={resetAll}>
                Redact Another File
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
  panelHeading: {
    fontSize: 16,
    fontWeight: 700,
    color: '#1a1a2e',
    marginBottom: 16,
    display: 'flex',
    alignItems: 'center',
    gap: 8
  },
  badge: {
    background: '#e8edff',
    color: '#4f7ef8',
    borderRadius: 12,
    padding: '2px 8px',
    fontSize: 12,
    fontWeight: 700
  },
  transcriptBox: {
    background: '#f8f9ff',
    borderRadius: 10,
    padding: '20px 20px',
    maxHeight: 480,
    overflowY: 'auto',
    border: '1px solid #e8ecff'
  },
  entityRow: {
    display: 'flex',
    alignItems: 'flex-start',
    padding: '10px 12px',
    borderRadius: 8,
    border: '1px solid #f0f0f0',
    cursor: 'pointer',
    transition: 'background 0.1s',
    background: '#fafafa'
  },
  categoryBadge: {
    fontSize: 11,
    fontWeight: 700,
    padding: '2px 7px',
    borderRadius: 6,
    border: '1px solid',
    letterSpacing: '0.02em',
    flexShrink: 0
  },
  primaryBtn: {
    background: '#4f7ef8',
    color: '#fff',
    border: 'none',
    borderRadius: 10,
    padding: '12px 24px',
    fontWeight: 600,
    fontSize: 15,
    cursor: 'pointer',
    transition: 'opacity 0.15s',
    width: '100%'
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
  },
  textBtn: {
    background: 'none',
    border: 'none',
    color: '#4f7ef8',
    cursor: 'pointer',
    fontSize: 12,
    padding: '0 4px'
  },
  audioContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
    background: '#f8f9ff',
    borderRadius: 12,
    padding: '20px'
  },
  downloadLink: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: '#f0f4ff',
    color: '#4f7ef8',
    padding: '10px 20px',
    borderRadius: 8,
    textDecoration: 'none',
    fontWeight: 600,
    fontSize: 14
  },
  select: {
    width: '100%',
    padding: '10px 12px',
    borderRadius: 8,
    border: '1px solid #d0d7ff',
    fontSize: 14,
    color: '#333',
    background: '#fff',
    cursor: 'pointer',
    outline: 'none'
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
  }
}
