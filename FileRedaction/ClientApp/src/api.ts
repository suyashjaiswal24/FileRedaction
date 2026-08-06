import axios from 'axios'
import type { UploadResponse, PreviewResponse, PiiEntity } from './types'

const http = axios.create({ baseURL: '/api/redaction' })

export async function uploadFile(file: File): Promise<UploadResponse> {
  const form = new FormData()
  form.append('file', file)
  const { data } = await http.post<UploadResponse>('/upload', form)
  return data
}

export async function getPreview(sessionId: string, entityIds: string[]): Promise<PreviewResponse> {
  const { data } = await http.post<PreviewResponse>('/preview', {
    sessionId,
    selectedEntityIds: entityIds
  })
  return data
}

export async function downloadRedacted(sessionId: string, entityIds: string[]): Promise<Blob> {
  const { data } = await http.post<Blob>(
    '/redact',
    { sessionId, selectedEntityIds: entityIds },
    { responseType: 'blob' }
  )
  return data
}

export async function addManualEntity(sessionId: string, text: string): Promise<PiiEntity> {
  const { data } = await http.post<PiiEntity>(`/${sessionId}/add-entity`, { text })
  return data
}

export interface WordMatch {
  text: string
  pageNumber: number
  polygon: number[]
  isPixelUnit: boolean
}

export async function searchWords(sessionId: string, q: string): Promise<WordMatch[]> {
  const { data } = await http.get<WordMatch[]>(`/${sessionId}/search-words`, { params: { q } })
  return data
}
