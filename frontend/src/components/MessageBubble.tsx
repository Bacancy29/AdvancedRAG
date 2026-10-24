import Box from '@mui/material/Box'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import Typography from '@mui/material/Typography'
import BlockIcon from '@mui/icons-material/Block'

interface Props {
  role: 'user' | 'assistant'
  content: string
  refused?: boolean
  streaming?: boolean
  naive?: boolean
  status?: string
  selected?: boolean
  onSelect?: () => void
  onCitationClick?: (n: number) => void
}

/** Renders an answer, turning [n] markers into clickable citation chips. */
export default function MessageBubble({ role, content, refused, streaming, naive, status, selected, onSelect, onCitationClick }: Props) {
  const isUser = role === 'user'
  const parts = content.split(/(\[\d+\])/g)

  return (
    <Box sx={{ display: 'flex', justifyContent: isUser ? 'flex-end' : 'flex-start', mb: 1.5 }}>
      <Paper
        onClick={onSelect}
        sx={{
          px: 2, py: 1.25, maxWidth: '85%',
          cursor: onSelect ? 'pointer' : 'default',
          bgcolor: isUser ? 'primary.main' : 'background.paper',
          color: isUser ? 'primary.contrastText' : 'text.primary',
          borderColor: selected ? 'primary.main' : 'divider',
          borderWidth: selected ? 2 : 1,
        }}
      >
        {(refused || naive) && (
          <Box sx={{ display: 'flex', gap: 0.75, mb: 0.75 }}>
            {naive && <Chip size="small" label="Naive pipeline" color="warning" variant="outlined" />}
            {refused && <Chip size="small" icon={<BlockIcon />} label="Refused: not in document" color="default" variant="outlined" />}
          </Box>
        )}
        <Typography variant="body2" component="div" sx={{ whiteSpace: 'pre-wrap', lineHeight: 1.7 }}>
          {parts.map((p, i) => {
            const m = /^\[(\d+)\]$/.exec(p)
            if (!m || isUser) return <span key={i}>{p}</span>
            const n = Number(m[1])
            return (
              <Chip key={i} label={n} size="small" color="primary" variant="outlined"
                onClick={e => { e.stopPropagation(); onCitationClick?.(n) }}
                sx={{ height: 20, mx: 0.25, '& .MuiChip-label': { px: 0.75, fontSize: 12 } }} />
            )
          })}
          {streaming && !content && status && (
            <Typography component="span" variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>{status}</Typography>
          )}
          {streaming && <span className="cursor">▍</span>}
        </Typography>
      </Paper>
    </Box>
  )
}
