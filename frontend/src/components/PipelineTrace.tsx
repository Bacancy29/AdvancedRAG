import { useState } from 'react'
import Accordion from '@mui/material/Accordion'
import AccordionDetails from '@mui/material/AccordionDetails'
import AccordionSummary from '@mui/material/AccordionSummary'
import Box from '@mui/material/Box'
import Typography from '@mui/material/Typography'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import type { StageTrace } from '../api/types'
import { stageColors } from '../theme'

/**
 * What each stage did for this question, in pipeline order. Ranked lists (dense, BM25, fused,
 * reranked) can be expanded; the chunk that was finally ranked first is highlighted in every list
 * so you can see how each technique moved it.
 */
export default function PipelineTrace({ trace }: { trace: StageTrace[] }) {
  const finalTop = [...trace].reverse().find(t => t.enabled && t.hits?.length)?.hits?.[0]?.chunkId
  const [hover, setHover] = useState<string | null>(null)

  return (
    <Box>
      {trace.map((t, i) => (
        <Accordion key={i} disableGutters elevation={0} defaultExpanded={false}
          disabled={!t.hits?.length}
          sx={{ border: 1, borderColor: 'divider', mb: 1, borderRadius: 1, '&:before': { display: 'none' }, opacity: t.enabled ? 1 : 0.55 }}>
          <AccordionSummary expandIcon={t.hits?.length ? <ExpandMoreIcon /> : null}
            sx={{ '&.Mui-disabled': { opacity: 1 } }}>
            <Box sx={{ display: 'flex', gap: 1.25, alignItems: 'flex-start', width: '100%' }}>
              <Box sx={{
                bgcolor: stageColors[t.stage] ?? 'grey.500', color: '#fff', fontSize: 10, fontWeight: 700,
                textTransform: 'uppercase', px: 0.75, py: 0.25, borderRadius: 0.75, minWidth: 82, textAlign: 'center', mt: 0.25,
              }}>{t.stage}</Box>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  {t.technique} {!t.enabled && <Typography component="span" variant="caption" color="text.secondary">(off)</Typography>}
                </Typography>
                <Typography variant="caption" color="text.secondary">{t.summary}</Typography>
              </Box>
              {t.elapsedMs > 0 && <Typography variant="caption" color="text.secondary">{t.elapsedMs} ms</Typography>}
            </Box>
          </AccordionSummary>
          {t.hits && t.hits.length > 0 && (
            <AccordionDetails sx={{ pt: 0 }}>
              {t.hits.map(h => {
                const isTop = h.chunkId === finalTop
                const isHover = h.chunkId === hover
                return (
                  <Box key={h.chunkId} onMouseEnter={() => setHover(h.chunkId)} onMouseLeave={() => setHover(null)}
                    sx={{
                      display: 'flex', gap: 1, py: 0.5, px: 0.75, borderRadius: 0.75, fontSize: 12,
                      bgcolor: isHover ? 'action.selected' : isTop ? 'action.hover' : 'transparent',
                      borderLeft: 3, borderColor: isTop ? 'success.main' : 'transparent',
                    }}>
                    <Box sx={{ width: 20, color: 'text.secondary' }}>#{h.rank}</Box>
                    <Box sx={{ width: 34, color: 'text.secondary' }}>p.{h.page}</Box>
                    <Box sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={h.preview}>
                      {h.preview}
                    </Box>
                    <Box sx={{ width: 52, textAlign: 'right', color: 'text.secondary' }}>{h.score.toFixed(3)}</Box>
                  </Box>
                )
              })}
            </AccordionDetails>
          )}
        </Accordion>
      ))}
    </Box>
  )
}
