import { useState, useRef, DragEvent, ChangeEvent } from 'react'
import { uploadFile } from '../api'
import type { UploadAcceptedResponse } from '../types'

interface Props {
  onUploadComplete: (result: UploadAcceptedResponse) => void
  initialError?: string
}

const ACCEPTED = '.pdf,.docx,.doc,.docm,.odt,.rtf,.xlsx,.xls,.ods,.pptx,.ppt,.odp,.png,.jpg,.jpeg,.tiff,.tif,.bmp,.gif,.webp'
const ACCEPTED_LABEL = 'PDF, Word, Excel, PowerPoint, Images'

export default function FileUpload({ onUploadComplete, initialError }: Props) {
  const [dragging, setDragging] = useState(false)
  const [status, setStatus] = useState<'idle' | 'uploading' | 'error'>(initialError ? 'error' : 'idle')
  const [errorMsg, setErrorMsg] = useState(initialError ?? '')
  const [progressLabel, setProgressLabel] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)

  async function handleFile(file: File) {
    setStatus('uploading')
    setProgressLabel('Uploading…')
    setErrorMsg('')
    try {
      const result = await uploadFile(file)
      onUploadComplete(result)
    } catch (err: unknown) {
      const msg = axios_message(err)
      setStatus('error')
      setErrorMsg(msg)
    }
  }

  function onDrop(e: DragEvent<HTMLDivElement>) {
    e.preventDefault()
    setDragging(false)
    const file = e.dataTransfer.files[0]
    if (file) handleFile(file)
  }

  function onInputChange(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (file) handleFile(file)
  }

  function axios_message(err: unknown): string {
    if (err && typeof err === 'object' && 'response' in err) {
      const r = (err as { response?: { data?: unknown } }).response
      if (r?.data) return typeof r.data === 'string' ? r.data : JSON.stringify(r.data)
    }
    return err instanceof Error ? err.message : 'Unknown error'
  }

  return (
    <div style={{ maxWidth: 560, margin: '60px auto' }}>
      <div style={styles.card}>
        <h2 style={styles.title}>Upload Document</h2>
        <p style={styles.subtitle}>
          Upload a file to extract and review PII before redacting it.
        </p>

        {status === 'uploading' ? (
          <div style={styles.loading}>
            <div style={styles.spinner} />
            <p style={{ marginTop: 16, color: '#555' }}>{progressLabel}</p>
          </div>
        ) : (
          <div
            style={{ ...styles.dropzone, ...(dragging ? styles.dropzoneDragging : {}) }}
            onDragOver={e => { e.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            onClick={() => inputRef.current?.click()}
          >
            <svg width="48" height="48" fill="none" viewBox="0 0 24 24" stroke="#4f7ef8" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5m-13.5-9L12 3m0 0l4.5 4.5M12 3v13.5" />
            </svg>
            <p style={{ marginTop: 12, fontWeight: 600, color: '#4f7ef8' }}>
              Drop your file here or click to browse
            </p>
            <p style={{ fontSize: 13, color: '#888', marginTop: 4 }}>Supports {ACCEPTED_LABEL}</p>
            <input
              ref={inputRef}
              type="file"
              accept={ACCEPTED}
              onChange={onInputChange}
              style={{ display: 'none' }}
            />
          </div>
        )}

        {status === 'error' && (
          <div style={styles.error}>
            <strong>Error:</strong> {errorMsg}
            <button style={styles.retryBtn} onClick={() => setStatus('idle')}>Try again</button>
          </div>
        )}
      </div>
    </div>
  )
}

const styles: Record<string, React.CSSProperties> = {
  card: {
    background: '#fff',
    borderRadius: 16,
    padding: '40px 36px',
    boxShadow: '0 4px 24px rgba(0,0,0,0.08)'
  },
  title: { fontSize: 22, fontWeight: 700, marginBottom: 8 },
  subtitle: { color: '#666', marginBottom: 28, fontSize: 14 },
  dropzone: {
    border: '2px dashed #c0cfe8',
    borderRadius: 12,
    padding: '48px 24px',
    textAlign: 'center',
    cursor: 'pointer',
    transition: 'border-color 0.2s, background 0.2s',
    background: '#fafcff'
  },
  dropzoneDragging: { borderColor: '#4f7ef8', background: '#eef3fe' },
  loading: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '40px 0'
  },
  spinner: {
    width: 44,
    height: 44,
    border: '4px solid #e0e7ff',
    borderTop: '4px solid #4f7ef8',
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite'
  },
  error: {
    marginTop: 16,
    padding: '12px 16px',
    background: '#fff0f0',
    border: '1px solid #fca5a5',
    borderRadius: 8,
    fontSize: 14,
    color: '#b91c1c',
    display: 'flex',
    alignItems: 'center',
    gap: 12
  },
  retryBtn: {
    marginLeft: 'auto',
    padding: '4px 12px',
    background: '#fff',
    border: '1px solid #fca5a5',
    borderRadius: 6,
    color: '#b91c1c',
    fontSize: 13
  }
}
