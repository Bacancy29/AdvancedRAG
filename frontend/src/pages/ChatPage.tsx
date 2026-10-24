import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import Box from '@mui/material/Box'
import CircularProgress from '@mui/material/CircularProgress'
import Typography from '@mui/material/Typography'
import { api } from '../api/client'
import ChatPanel, { type UiMessage } from '../components/ChatPanel'
import DocumentSidebar from '../components/DocumentSidebar'
import SourcesPanel from '../components/SourcesPanel'

export default function ChatPage() {
  const { docId } = useParams()
  const navigate = useNavigate()
  const documents = useQuery({ queryKey: ['documents'], queryFn: api.documents })
  const [selected, setSelected] = useState<UiMessage | null>(null)
  const [highlight, setHighlight] = useState<number | null>(null)

  const docs = documents.data ?? []
  const current = docs.find(d => d.id === Number(docId)) ?? null

  // Default to the first ready document.
  useEffect(() => {
    if (!docId && docs.length > 0) navigate(`/chat/${(docs.find(d => d.status === 'Ready') ?? docs[0]).id}`, { replace: true })
  }, [docId, docs, navigate])

  useEffect(() => { setSelected(null); setHighlight(null) }, [docId])

  if (documents.isLoading) return <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress /></Box>
  if (docs.length === 0) return <Typography sx={{ p: 4 }}>No documents yet. Add one on the Documents page.</Typography>

  return (
    <Box sx={{
      height: '100%', display: 'grid', minHeight: 0,
      gridTemplateColumns: { xs: '1fr', md: '250px 1fr', lg: '260px minmax(0,1fr) 440px' },
      gridTemplateRows: { xs: 'auto 1fr', md: '1fr' },
    }}>
      <Box sx={{ display: { xs: 'none', md: 'block' }, minHeight: 0 }}>
        <DocumentSidebar documents={docs} selectedId={current?.id ?? null} onSelect={id => navigate(`/chat/${id}`)} />
      </Box>
      <Box sx={{ minHeight: 0 }}>
        {current && (
          <ChatPanel key={current.id} document={current} selectedKey={selected?.key ?? null}
            onSelectAnswer={m => { setSelected(m); setHighlight(null) }}
            onCitationClick={(m, n) => { setSelected(m); setHighlight(n) }} />
        )}
      </Box>
      <Box sx={{ display: { xs: 'none', lg: 'block' }, minHeight: 0 }}>
        <SourcesPanel documentId={current?.id ?? 0} citations={selected?.citations ?? []} trace={selected?.trace ?? []} highlight={highlight} />
      </Box>
    </Box>
  )
}
