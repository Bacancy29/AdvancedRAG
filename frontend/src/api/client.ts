import type {
  Citation, ChunkRow, DocumentProfile, EvalReport, EvalSet, Health, RagAnswer, RagDocument, ScorecardRow, StageInfo, StageTrace, StoredMessage,
} from './types'

async function http<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) {
    const body = await res.text()
    let message = body
    try { message = JSON.parse(body).error ?? body } catch { /* plain text */ }
    throw new Error(message || `${res.status} ${res.statusText}`)
  }
  if (res.status === 204 || res.status === 202) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  health: () => http<Health>('/api/health'),
  pipeline: () => http<StageInfo[]>('/api/pipeline'),

  documents: () => http<RagDocument[]>('/api/documents'),
  document: (id: number) => http<RagDocument>(`/api/documents/${id}`),
  ingest: (id: number) => http<void>(`/api/documents/${id}/ingest`, { method: 'POST' }),
  deleteDocument: (id: number) => http<void>(`/api/documents/${id}`, { method: 'DELETE' }),
  upload: (file: File, name: string, profile: DocumentProfile) => {
    const form = new FormData()
    form.append('file', file)
    form.append('name', name)
    form.append('profile', profile)
    return http<{ id: number }>('/api/documents', { method: 'POST', body: form })
  },
  chunks: (id: number, set: 'advanced' | 'naive' = 'advanced') =>
    http<ChunkRow[]>(`/api/documents/${id}/chunks?set=${set}`),

  messages: (id: number) => http<StoredMessage[]>(`/api/documents/${id}/messages`),
  clearMessages: (id: number) => http<void>(`/api/documents/${id}/messages`, { method: 'DELETE' }),

  scorecard: () => http<ScorecardRow[]>('/api/scorecard'),
  evalQuestions: (id: number) => http<EvalSet>(`/api/documents/${id}/eval/questions`),
  runEval: (id: number, answers: boolean, resume = false) =>
    http<EvalReport>(`/api/documents/${id}/eval?answers=${answers}&resume=${resume}`, { method: 'POST' }),
  latestEval: async (id: number): Promise<EvalReport | null> => {
    const res = await fetch(`/api/documents/${id}/eval/latest`)
    return res.ok ? res.json() : null
  },
}

export interface StreamHandlers {
  /** Retrieval finished: sources are known before the answer is written. */
  onRetrieved?: (citations: Citation[], trace: StageTrace[]) => void
  onToken: (text: string) => void
  onDone: (answer: RagAnswer) => void
  onError: (message: string) => void
}

/** POST /api/chat and read the Server-Sent Events stream: "token" events, then "done" (or "error"). */
export async function streamChat(
  documentId: number, question: string, naive: boolean, handlers: StreamHandlers, signal?: AbortSignal,
) {
  const res = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ documentId, question, naive }),
    signal,
  })
  if (!res.ok || !res.body) {
    handlers.onError(`Request failed: ${res.status}`)
    return
  }

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let sep: number
    while ((sep = buffer.indexOf('\n\n')) >= 0) {
      const raw = buffer.slice(0, sep)
      buffer = buffer.slice(sep + 2)
      const event = /^event: (.*)$/m.exec(raw)?.[1]
      const data = /^data: (.*)$/m.exec(raw)?.[1]
      if (!event || !data) continue
      const payload = JSON.parse(data)
      if (event === 'retrieved') handlers.onRetrieved?.(payload.citations, payload.trace)
      else if (event === 'token') handlers.onToken(payload.text)
      else if (event === 'done') handlers.onDone(payload)
      else if (event === 'error') handlers.onError(payload.error)
    }
  }
}
