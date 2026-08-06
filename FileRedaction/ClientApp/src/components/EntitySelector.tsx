import { useState, useEffect, useRef, KeyboardEvent } from 'react'
import type { PiiEntity } from '../types'
import { addManualEntity, searchWords } from '../api'
import type { WordMatch } from '../api'

interface Props {
  sessionId: string
  entities: PiiEntity[]
  selectedIds: Set<string>
  onSelectionChange: (ids: Set<string>) => void
  onEntityAdded: (entity: PiiEntity) => void
  onNext: () => void
  fileName: string
}

const CATEGORY_COLORS: Record<string, string> = {
  Person: '#e0f0ff', Organization: '#e6f9ee', PhoneNumber: '#fff8e1',
  Email: '#fce4ec', Address: '#f3e5f5', DateTime: '#e8f5e9',
  IPAddress: '#fff3e0', URL: '#e3f2fd', CreditCardNumber: '#fce4ec',
  USSocialSecurityNumber: '#fce4ec', Manual: '#fdf4e7', Default: '#f5f5f5'
}
const CATEGORY_TEXT: Record<string, string> = {
  Person: '#1565c0', Organization: '#2e7d32', PhoneNumber: '#e65100',
  Email: '#880e4f', Address: '#6a1b9a', DateTime: '#1b5e20',
  IPAddress: '#bf360c', URL: '#0d47a1', CreditCardNumber: '#880e4f',
  USSocialSecurityNumber: '#880e4f', Manual: '#92400e', Default: '#333'
}

function categoryStyle(cat: string): React.CSSProperties {
  return {
    background: CATEGORY_COLORS[cat] ?? CATEGORY_COLORS.Default,
    color: CATEGORY_TEXT[cat] ?? CATEGORY_TEXT.Default,
    padding: '2px 10px', borderRadius: 20, fontSize: 12, fontWeight: 600
  }
}

export default function EntitySelector({
  sessionId, entities, selectedIds, onSelectionChange, onEntityAdded, onNext, fileName
}: Props) {
  const [manualInput, setManualInput] = useState('')
  const [manualStatus, setManualStatus] = useState<'idle' | 'adding' | 'error'>('idle')
  const [manualError, setManualError] = useState('')
  const [wordMatches, setWordMatches] = useState<WordMatch[]>([])
  const [showDropdown, setShowDropdown] = useState(false)
  const searchTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const dropdownRef = useRef<HTMLDivElement>(null)

  const grouped = entities.reduce<Record<string, PiiEntity[]>>((acc, e) => {
    (acc[e.category] ??= []).push(e)
    return acc
  }, {})

  // Live word search with debounce
  useEffect(() => {
    if (searchTimer.current) clearTimeout(searchTimer.current)
    if (manualInput.trim().length < 2) { setWordMatches([]); setShowDropdown(false); return }
    searchTimer.current = setTimeout(async () => {
      try {
        const matches = await searchWords(sessionId, manualInput.trim())
        setWordMatches(matches)
        setShowDropdown(matches.length > 0)
      } catch { setWordMatches([]); setShowDropdown(false) }
    }, 300)
    return () => { if (searchTimer.current) clearTimeout(searchTimer.current) }
  }, [manualInput, sessionId])

  // Close dropdown on outside click
  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node))
        setShowDropdown(false)
    }
    document.addEventListener('mousedown', handleClick)
    return () => document.removeEventListener('mousedown', handleClick)
  }, [])

  function toggle(id: string) {
    const next = new Set(selectedIds)
    next.has(id) ? next.delete(id) : next.add(id)
    onSelectionChange(next)
  }

  function toggleCategory(cat: string) {
    const catIds = (grouped[cat] ?? []).map(e => e.id)
    const allSelected = catIds.every(id => selectedIds.has(id))
    const next = new Set(selectedIds)
    catIds.forEach(id => allSelected ? next.delete(id) : next.add(id))
    onSelectionChange(next)
  }

  function selectAll() { onSelectionChange(new Set(entities.map(e => e.id))) }
  function clearAll() { onSelectionChange(new Set()) }

  async function handleAdd(text: string) {
    const trimmed = text.trim()
    if (!trimmed) return
    setManualStatus('adding')
    setManualError('')
    setShowDropdown(false)
    try {
      const entity = await addManualEntity(sessionId, trimmed)
      onEntityAdded(entity)
      onSelectionChange(new Set([...selectedIds, entity.id]))
      setManualInput('')
      setWordMatches([])
      setManualStatus('idle')
    } catch (err: unknown) {
      setManualError(axiosMessage(err))
      setManualStatus('error')
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter') handleAdd(manualInput)
    if (e.key === 'Escape') setShowDropdown(false)
  }

  function axiosMessage(err: unknown): string {
    if (err && typeof err === 'object' && 'response' in err) {
      const r = (err as { response?: { data?: unknown } }).response
      if (r?.data) return typeof r.data === 'string' ? r.data : JSON.stringify(r.data)
    }
    return err instanceof Error ? err.message : 'Unknown error'
  }

  const selectedCount = selectedIds.size
  const manualCount = (grouped['Manual'] ?? []).length

  return (
    <div style={{ maxWidth: 800, margin: '40px auto' }}>
      <div style={styles.card}>
        {/* Header */}
        <div style={styles.header}>
          <div>
            <h2 style={styles.title}>Select PII to Redact</h2>
            <p style={styles.subtitle}>{fileName} — {entities.length} entities detected</p>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button style={styles.linkBtn} onClick={selectAll}>Select all</button>
            <button style={styles.linkBtn} onClick={clearAll}>Clear all</button>
          </div>
        </div>

        {/* Detected entities */}
        {entities.filter(e => e.category !== 'Manual').length === 0 && manualCount === 0 ? (
          <div style={styles.empty}>
            <p>No PII entities were automatically detected.</p>
            <p style={{ fontSize: 13, marginTop: 6 }}>Use the manual search below to add words to redact.</p>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
            {Object.entries(grouped)
              .filter(([cat]) => cat !== 'Manual')
              .map(([category, items]) => renderGroup(category, items))}
          </div>
        )}

        {/* Manual add section */}
        <div style={styles.manualSection}>
          <div style={styles.manualHeader}>
            <svg width="16" height="16" fill="none" viewBox="0 0 24 24" stroke="#92400e" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-4.35-4.35M17 11A6 6 0 1 1 5 11a6 6 0 0 1 12 0z" />
            </svg>
            <span style={{ fontWeight: 600, color: '#92400e' }}>Add words manually</span>
            <span style={{ fontSize: 12, color: '#a16207', marginLeft: 4 }}>— for text the AI didn't detect</span>
          </div>

          <div style={{ position: 'relative' }} ref={dropdownRef}>
            <div style={styles.manualInputRow}>
              <input
                type="text"
                value={manualInput}
                onChange={e => { setManualInput(e.target.value); setManualStatus('idle') }}
                onKeyDown={onKeyDown}
                onFocus={() => wordMatches.length > 0 && setShowDropdown(true)}
                placeholder="Type to search extracted words, or enter any text…"
                style={styles.manualInput}
                disabled={manualStatus === 'adding'}
              />
              <button
                style={{ ...styles.addBtn, opacity: !manualInput.trim() || manualStatus === 'adding' ? 0.5 : 1 }}
                onClick={() => handleAdd(manualInput)}
                disabled={!manualInput.trim() || manualStatus === 'adding'}
              >
                {manualStatus === 'adding' ? 'Adding…' : '+ Add'}
              </button>
            </div>

            {/* Word search dropdown */}
            {showDropdown && (
              <div style={styles.dropdown}>
                <div style={styles.dropdownHint}>Extracted words matching "{manualInput}" — click to add</div>
                {wordMatches.map((w, i) => (
                  <button
                    key={i}
                    style={styles.dropdownItem}
                    onMouseDown={e => { e.preventDefault(); handleAdd(w.text) }}
                  >
                    <span style={{ fontWeight: 500 }}>{w.text}</span>
                    <span style={{ fontSize: 11, color: '#999', marginLeft: 8 }}>page {w.pageNumber}</span>
                  </button>
                ))}
              </div>
            )}
          </div>

          {manualStatus === 'error' && <p style={styles.manualError}>{manualError}</p>}

          {manualCount > 0 && (
            <div style={{ marginTop: 12 }}>
              {renderGroup('Manual', grouped['Manual'] ?? [])}
            </div>
          )}
        </div>

        {/* Footer */}
        <div style={styles.footer}>
          <span style={{ color: '#666', fontSize: 14 }}>
            {selectedCount} of {entities.length} entities selected
          </span>
          <button
            style={{ ...styles.primaryBtn, opacity: selectedCount === 0 ? 0.5 : 1 }}
            disabled={selectedCount === 0}
            onClick={onNext}
          >
            Preview Highlights →
          </button>
        </div>
      </div>
    </div>
  )

  function renderGroup(category: string, items: PiiEntity[]) {
    const allChecked = items.every(e => selectedIds.has(e.id))
    const someChecked = items.some(e => selectedIds.has(e.id))
    return (
      <div key={category} style={styles.group}>
        <div style={styles.groupHeader}>
          <label style={styles.groupLabel}>
            <input
              type="checkbox"
              checked={allChecked}
              ref={el => { if (el) el.indeterminate = !allChecked && someChecked }}
              onChange={() => toggleCategory(category)}
              style={{ width: 16, height: 16, cursor: 'pointer' }}
            />
            <span style={categoryStyle(category)}>{category}</span>
            <span style={{ color: '#888', fontSize: 13 }}>{items.length} item{items.length > 1 ? 's' : ''}</span>
          </label>
        </div>
        <div style={styles.entityList}>
          {items.map(entity => (
            <label key={entity.id} style={{
              ...styles.entityRow,
              background: selectedIds.has(entity.id) ? '#f0f4ff' : '#fafafa'
            }}>
              <input
                type="checkbox"
                checked={selectedIds.has(entity.id)}
                onChange={() => toggle(entity.id)}
                style={{ width: 16, height: 16, cursor: 'pointer', flexShrink: 0 }}
              />
              <span style={styles.entityText}>{entity.text}</span>
              {entity.subCategory && (
                <span style={{ color: '#aaa', fontSize: 12, marginLeft: 4 }}>({entity.subCategory})</span>
              )}
              {entity.category !== 'Manual' ? (
                <span style={{ marginLeft: 'auto', fontSize: 12, color: '#888', background: '#f0f0f0', padding: '1px 8px', borderRadius: 10 }}>
                  {entity.occurrenceCount} {entity.occurrenceCount === 1 ? 'occurrence' : 'occurrences'}
                </span>
              ) : (
                <span style={{ marginLeft: 'auto', fontSize: 11, color: '#a16207', background: '#fef3c7', padding: '1px 8px', borderRadius: 12 }}>
                  manual
                </span>
              )}
              {entity.boundingRegions.length > 0 && (
                <span style={{ fontSize: 12, color: '#aaa', marginLeft: 8 }}>
                  p.{[...new Set(entity.boundingRegions.map(r => r.pageNumber))].join(',')}
                </span>
              )}
            </label>
          ))}
        </div>
      </div>
    )
  }
}

const styles: Record<string, React.CSSProperties> = {
  card: { background: '#fff', borderRadius: 16, padding: '32px 36px', boxShadow: '0 4px 24px rgba(0,0,0,0.08)' },
  header: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 24 },
  title: { fontSize: 22, fontWeight: 700 },
  subtitle: { color: '#666', fontSize: 14, marginTop: 4 },
  linkBtn: { background: 'none', border: '1px solid #ddd', borderRadius: 6, padding: '5px 12px', fontSize: 13, color: '#555', cursor: 'pointer' },
  group: { border: '1px solid #eee', borderRadius: 10, overflow: 'hidden' },
  groupHeader: { background: '#f7f9fc', padding: '10px 16px', borderBottom: '1px solid #eee' },
  groupLabel: { display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', userSelect: 'none' },
  entityList: { display: 'flex', flexDirection: 'column', gap: 0 },
  entityRow: {
    display: 'flex', alignItems: 'center', gap: 10, padding: '10px 16px',
    cursor: 'pointer', borderBottom: '1px solid #f0f0f0', transition: 'background 0.1s', userSelect: 'none'
  },
  entityText: { fontWeight: 500, fontSize: 14, flex: 1 },
  manualSection: {
    marginTop: 28, paddingTop: 20, borderTop: '2px dashed #fcd34d',
    background: '#fffbeb', borderRadius: 10, padding: '20px', marginLeft: -4, marginRight: -4
  },
  manualHeader: { display: 'flex', alignItems: 'center', gap: 6, marginBottom: 12 },
  manualInputRow: { display: 'flex', gap: 8 },
  manualInput: {
    flex: 1, padding: '9px 14px', border: '1px solid #d97706', borderRadius: 8,
    fontSize: 14, outline: 'none', background: '#fff'
  },
  addBtn: {
    padding: '9px 20px', background: '#d97706', color: '#fff', border: 'none',
    borderRadius: 8, fontWeight: 600, fontSize: 14, cursor: 'pointer'
  },
  dropdown: {
    position: 'absolute', top: '100%', left: 0, right: 60, zIndex: 100,
    background: '#fff', border: '1px solid #fcd34d', borderRadius: 8,
    boxShadow: '0 4px 16px rgba(0,0,0,0.12)', maxHeight: 220, overflowY: 'auto', marginTop: 2
  },
  dropdownHint: { padding: '6px 12px', fontSize: 11, color: '#a16207', background: '#fffbeb', borderBottom: '1px solid #fef3c7' },
  dropdownItem: {
    display: 'flex', alignItems: 'center', width: '100%', padding: '8px 14px',
    background: 'none', border: 'none', borderBottom: '1px solid #fef9ee',
    textAlign: 'left', cursor: 'pointer', fontSize: 14, color: '#333'
  },
  manualError: { color: '#b45309', fontSize: 13, marginTop: 6 },
  footer: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginTop: 28, paddingTop: 20, borderTop: '1px solid #eee' },
  primaryBtn: {
    background: '#4f7ef8', color: '#fff', border: 'none', borderRadius: 10,
    padding: '10px 24px', fontWeight: 600, fontSize: 15, cursor: 'pointer'
  },
  empty: { padding: '24px 0', textAlign: 'center', color: '#888' }
}
