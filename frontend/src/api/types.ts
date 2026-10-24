// Mirrors the C# records returned by Rag.Api (camelCase JSON).

export type DocumentProfile = 'PlainText' | 'LongReport' | 'Visual'
export type DocumentStatus = 'NotIngested' | 'Ingesting' | 'Ready' | 'Failed'

export interface IngestProgress {
  stage: string
  percent: number
  message: string
}

export interface RagDocument {
  id: number
  name: string
  profile: DocumentProfile
  status: DocumentStatus
  pageCount: number
  chunkCount: number
  embeddingModel: string | null
  error: string | null
  createdAt: string
  ingestedAt: string | null
  fileName: string
  hasEvalSet: boolean
  progress: IngestProgress | null
}

export interface TraceHit {
  chunkId: string
  rank: number
  score: number
  page: number
  section: string
  preview: string
}

export interface StageTrace {
  stage: string
  technique: string
  enabled: boolean
  summary: string
  hits: TraceHit[] | null
  elapsedMs: number
}

export interface Citation {
  number: number
  chunkId: string
  page: number
  section: string
  contentType: string
  text: string
  score: number
  cited: boolean
}

export interface RagAnswer {
  answer: string
  refused: boolean
  citations: Citation[]
  trace: StageTrace[]
}

export interface StoredMessage {
  id: number
  role: 'user' | 'assistant'
  content: string
  createdAt: string
  meta: { refused: boolean; citations: Citation[]; trace: StageTrace[] } | null
}

export interface ChunkRow {
  id: string
  chunkIndex: number
  contentType: string
  pageNumber: number
  pageEnd: number
  sectionPath: string
  tokenCount: number
  text: string
}

export interface TechniqueInfo {
  stage: string
  name: string
  description: string
  implemented: boolean
  profiles: DocumentProfile[]
}

export interface StageInfo {
  number: number
  name: string
  when: string
  techniques: TechniqueInfo[]
}

export interface Health {
  database: string
  vectorSearch: string
  chatModel: string
  chatConfigured: boolean
  embeddingModel: string
  reranker: string | null
}

export interface EvalQuestion {
  id: string
  question: string
  category: string
  required: boolean
  trap: boolean
  expected: string | null
}

export interface EvalSet {
  document: string
  questions: EvalQuestion[]
}

export interface LadderStep {
  name: string
  description: string
  mrr: number
  hitAt5: number
  ranks: (number | null)[]
  sectionHit: number | null
}

export interface AnswerCheck {
  id: string
  question: string
  category: string
  required: boolean
  trap: boolean
  expected: string | null
  answer: string
  refused: boolean
  passed: boolean
  rank: number | null
  citedPages: number[]
  error: string | null
  failReason: string | null
  topSection: string | null
}

export interface EvalReport {
  documentId: number
  document: string
  createdAt: string
  chatModel: string
  embeddingModel: string
  vectorSearch: string
  ladder: LadderStep[]
  answers: AnswerCheck[]
  passed: number
  total: number
  requiredPassed: boolean
  elapsedMs: number
}

export interface ScorecardRow {
  documentId: number
  name: string
  profile: DocumentProfile
  createdAt: string
  chatModel: string
  naiveMrr: number
  advancedMrr: number
  naiveHitAt5: number
  advancedHitAt5: number
  sectionHit: number | null
  passed: number
  total: number
  requiredPassed: boolean
  unanswered: number
}
