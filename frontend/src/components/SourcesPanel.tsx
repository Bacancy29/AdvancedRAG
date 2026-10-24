import { useEffect, useRef, useState } from 'react'
import Box from '@mui/material/Box'
import Tab from '@mui/material/Tab'
import Tabs from '@mui/material/Tabs'
import Typography from '@mui/material/Typography'
import type { Citation, StageTrace } from '../api/types'
import PipelineTrace from './PipelineTrace'
import SourceCard from './SourceCard'
import { SHOW_DEV_VIEWS } from '../devViews'

interface Props {
  documentId: number
  citations: Citation[]
  trace: StageTrace[]
  highlight: number | null
}

export default function SourcesPanel({ documentId, citations, trace, highlight }: Props) {
  const [tab, setTab] = useState(0)
  const refs = useRef<Record<number, HTMLDivElement | null>>({})

  useEffect(() => {
    if (highlight == null) return
    setTab(0)
    requestAnimationFrame(() => refs.current[highlight]?.scrollIntoView({ behavior: 'smooth', block: 'center' }))
  }, [highlight])

  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderLeft: 1, borderColor: 'divider' }}>
      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ borderBottom: 1, borderColor: 'divider', px: 1 }}>
        <Tab label={`Sources (${citations.length})`} />
        {SHOW_DEV_VIEWS && <Tab label="Pipeline" />}
      </Tabs>
      <Box sx={{ flex: 1, overflow: 'auto', p: 1.5 }}>
        {citations.length === 0 && trace.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ p: 2, textAlign: 'center' }}>
            Ask a question. The passages used for the answer appear here.
          </Typography>
        )}
        {tab === 0 && citations.map(c => (
          <SourceCard key={c.chunkId} citation={c} documentId={documentId} highlighted={c.number === highlight}
            ref={el => { refs.current[c.number] = el }} />
        ))}
        {tab === 1 && <PipelineTrace trace={trace} />}
      </Box>
    </Box>
  )
}
