import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import Box from '@mui/material/Box'
import Chip from '@mui/material/Chip'
import FormControlLabel from '@mui/material/FormControlLabel'
import IconButton from '@mui/material/IconButton'
import Stack from '@mui/material/Stack'
import Switch from '@mui/material/Switch'
import TextField from '@mui/material/TextField'
import Tooltip from '@mui/material/Tooltip'
import Typography from '@mui/material/Typography'
import DeleteSweepIcon from '@mui/icons-material/DeleteSweep'
import SendIcon from '@mui/icons-material/Send'
import StopIcon from '@mui/icons-material/Stop'
import { api, streamChat } from '../api/client'
import type { Citation, RagDocument, StageTrace } from '../api/types'
import MessageBubble from './MessageBubble'
import { SHOW_DEV_VIEWS } from '../devViews'
import { ProfileChip } from './ProfileChip'

export interface UiMessage {
  key: string
  role: 'user' | 'assistant'
  content: string
  refused?: boolean
  naive?: boolean
  streaming?: boolean
  /** Shown in the bubble while waiting for the first words. */
  status?: string
  citations?: Citation[]
  trace?: StageTrace[]
}

interface Props {
  document: RagDocument
  selectedKey: string | null
  onSelectAnswer: (m: UiMessage) => void
  onCitationClick: (m: UiMessage, n: number) => void
}

export default function ChatPanel({ document, selectedKey, onSelectAnswer, onCitationClick }: Props) {
  const queryClient = useQueryClient()
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [input, setInput] = useState('')
  const [naive, setNaive] = useState(false)
  const [busy, setBusy] = useState(false)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)

  const history = useQuery({ queryKey: ['messages', document.id], queryFn: () => api.messages(document.id) })
  const evalSet = useQuery({
    queryKey: ['evalQuestions', document.id], queryFn: () => api.evalQuestions(document.id), enabled: document.hasEvalSet,
  })

  // Load saved history when the document changes.
  useEffect(() => {
    if (!history.data) return
    const loaded: UiMessage[] = history.data.map(m => ({
      key: `db-${m.id}`, role: m.role, content: m.content,
      refused: m.meta?.refused, citations: m.meta?.citations, trace: m.meta?.trace,
    }))
    setMessages(loaded)
    const last = [...loaded].reverse().find(m => m.role === 'assistant')
    if (last) onSelectAnswer(last)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [history.data])

  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }) }, [messages])

  const ask = async (question: string) => {
    if (!question.trim() || busy) return
    setInput('')
    setBusy(true)
    const id = crypto.randomUUID()
    const answerKey = `a-${id}`
    setMessages(m => [...m,
      { key: `q-${id}`, role: 'user', content: question },
      { key: answerKey, role: 'assistant', content: '', streaming: true, naive, status: 'Searching the document…' }])

    const update = (patch: Partial<UiMessage>) =>
      setMessages(m => m.map(x => (x.key === answerKey ? { ...x, ...patch } : x)))

    abort.current = new AbortController()
    try {
      await streamChat(document.id, question, naive, {
        onRetrieved: (citations, trace) => {
          // Sources are ready before the answer: show them now instead of an empty wait.
          const partial: UiMessage = {
            key: answerKey, role: 'assistant', content: '', streaming: true, naive, citations, trace,
            status: `Found  passages · writing the answer…`,
          }
          update(partial)
          onSelectAnswer(partial)
        },
        onToken: text => setMessages(m => m.map(x => (x.key === answerKey ? { ...x, content: x.content + text } : x))),
        onDone: answer => {
          const done: UiMessage = {
            key: answerKey, role: 'assistant', content: answer.answer, refused: answer.refused, naive,
            citations: answer.citations, trace: answer.trace, streaming: false,
          }
          update(done)
          onSelectAnswer(done)
        },
        onError: error => update({ content: `Error: ${error}`, streaming: false }),
      }, abort.current.signal)
    } catch (e) {
      if ((e as Error).name !== 'AbortError') update({ content: `Error: ${(e as Error).message}`, streaming: false })
      else update({ streaming: false })
    } finally {
      setBusy(false)
    }
  }

  const clear = async () => {
    await api.clearMessages(document.id)
    setMessages([])
    queryClient.invalidateQueries({ queryKey: ['messages', document.id] })
  }

  const suggestions = evalSet.data?.questions.filter(q => q.required || q.trap).slice(0, 6) ?? []
  const ready = document.status === 'Ready'

  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', minWidth: 0 }}>
      <Box sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 700 }} noWrap>{document.name}</Typography>
        <ProfileChip profile={document.profile} />
        <Box sx={{ flex: 1 }} />
        {SHOW_DEV_VIEWS && (
          <Tooltip title="Answer with the naive baseline (1,000-character chunks, dense search only) to compare">
            <FormControlLabel control={<Switch size="small" checked={naive} onChange={e => setNaive(e.target.checked)} />}
              label={<Typography variant="caption">Naive pipeline</Typography>} />
          </Tooltip>
        )}
        <Tooltip title="Clear chat history">
          <span><IconButton size="small" onClick={clear} disabled={busy || messages.length === 0}><DeleteSweepIcon /></IconButton></span>
        </Tooltip>
      </Box>

      <Box sx={{ flex: 1, overflow: 'auto', p: 2 }}>
        {!ready && (
          <Typography color="text.secondary" sx={{ textAlign: 'center', mt: 6 }}>
            This document is not ingested yet. Open <b>Documents</b> and click <b>Ingest</b>.
          </Typography>
        )}
        {ready && messages.length === 0 && (
          <Box sx={{ textAlign: 'center', mt: 6 }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Ask about {document.name}</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
              Answers come only from the document, with page citations. If it is not in the document, the assistant says so.
            </Typography>
            <Stack direction="row" sx={{ flexWrap: 'wrap', gap: 1, justifyContent: 'center', maxWidth: 720, mx: 'auto' }}>
              {suggestions.map(q => (
                <Chip key={q.id} label={q.question} onClick={() => ask(q.question)} variant="outlined"
                  color={q.trap ? 'warning' : 'primary'} sx={{ height: 'auto', py: 0.75, '& .MuiChip-label': { whiteSpace: 'normal' } }} />
              ))}
            </Stack>
          </Box>
        )}
        {messages.map(m => (
          <MessageBubble key={m.key} role={m.role} content={m.content} refused={m.refused} streaming={m.streaming}
            naive={m.naive} status={m.status} selected={m.key === selectedKey}
            onSelect={m.role === 'assistant' && m.citations ? () => onSelectAnswer(m) : undefined}
            onCitationClick={n => onCitationClick(m, n)} />
        ))}
        <div ref={bottom} />
      </Box>

      <Box component="form" onSubmit={e => { e.preventDefault(); ask(input) }}
        sx={{ p: 1.5, borderTop: 1, borderColor: 'divider', display: 'flex', gap: 1 }}>
        <TextField fullWidth size="small" placeholder={ready ? 'Ask about this document…' : 'Ingest the document first'}
          value={input} onChange={e => setInput(e.target.value)} disabled={!ready} autoComplete="off" />
        {busy
          ? <IconButton color="error" onClick={() => abort.current?.abort()}><StopIcon /></IconButton>
          : <IconButton color="primary" type="submit" disabled={!ready || !input.trim()}><SendIcon /></IconButton>}
      </Box>
    </Box>
  )
}
