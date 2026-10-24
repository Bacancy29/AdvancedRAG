import { forwardRef, useState } from 'react'
import Box from '@mui/material/Box'
import Breadcrumbs from '@mui/material/Breadcrumbs'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import Table from '@mui/material/Table'
import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import Typography from '@mui/material/Typography'
import type { Citation } from '../api/types'

const typeLabels: Record<string, string> = {
  text: 'Text',
  aside: 'Caption / panel',
  table: 'Table',
  chart: 'Figure (vision)',
  footnote: 'Endnote',
  summary: 'Chapter summary (AI)',
}

const typeColors: Record<string, 'default' | 'secondary' | 'success' | 'warning' | 'info'> = {
  table: 'success',
  chart: 'info',
  footnote: 'warning',
  summary: 'secondary',
}

const PREVIEW_CHARS = 700

/**
 * One retrieved passage. Rendering is driven by contentType: text and sections as prose, tables as a
 * real table, figures with the page image the vision model described, footnotes highlighted.
 */
const SourceCard = forwardRef<HTMLDivElement, { citation: Citation; highlighted: boolean; documentId: number }>(
  ({ citation: c, highlighted, documentId }, ref) => {
    // Long-report passages are whole parent sections (small-to-big): show a preview first.
    const [expanded, setExpanded] = useState(false)

    return (
      <Paper ref={ref} sx={{
        p: 1.5, mb: 1.25,
        borderColor: highlighted ? 'primary.main' : c.cited ? 'primary.light' : 'divider',
        borderWidth: highlighted ? 2 : 1,
        transition: 'border-color .2s',
      }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, mb: 0.75, flexWrap: 'wrap' }}>
          <Chip size="small" label={`[${c.number}]`} color={c.cited ? 'primary' : 'default'} />
          <Typography variant="caption" sx={{ fontWeight: 600 }}>Page {c.page}</Typography>
          <Chip size="small" variant="outlined" label={typeLabels[c.contentType] ?? c.contentType}
            color={typeColors[c.contentType] ?? 'default'} />
          <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>score {c.score.toFixed(3)}</Typography>
        </Box>
        {c.section && (
          <Breadcrumbs separator="›" sx={{ mb: 0.75, '& .MuiBreadcrumbs-li': { fontSize: 12 } }}>
            {c.section.split(' > ').map((part, i, all) => (
              <Typography key={i} variant="caption" color={i === all.length - 1 ? 'text.primary' : 'text.secondary'}
                sx={{ fontWeight: i === all.length - 1 ? 600 : 400 }}>{part}</Typography>
            ))}
          </Breadcrumbs>
        )}

        {c.contentType === 'table'
          ? <MarkdownTable text={c.text} />
          : c.contentType === 'chart'
            ? <Figure documentId={documentId} page={c.page} text={c.text} expanded={expanded} onToggle={() => setExpanded(e => !e)} />
            : <Prose text={c.text} expanded={expanded} onToggle={() => setExpanded(e => !e)} />}
      </Paper>
    )
  })

export default SourceCard

/** Body text; "Footnote n: …" lines appended at ingestion are shown as notes under the passage. */
function Prose({ text, expanded, onToggle }: { text: string; expanded: boolean; onToggle: () => void }) {
  const [body, ...footnotes] = text.split('\nFootnote ')
  const long = body.length > PREVIEW_CHARS
  const shown = long && !expanded ? body.slice(0, PREVIEW_CHARS) + '…' : body
  return (
    <>
      <Typography variant="body2" sx={{ fontSize: 13, lineHeight: 1.6 }}>{renderMarkers(shown)}</Typography>
      {long && (
        <Button size="small" onClick={onToggle} sx={{ mt: 0.5, px: 0 }}>
          {expanded ? 'Show less' : `Show full section (${Math.round(body.length / 4)} tokens)`}
        </Button>
      )}
      {footnotes.map(f => (
        <Box key={f} sx={{ mt: 1, pl: 1, borderLeft: 3, borderColor: 'warning.main' }}>
          <Typography variant="caption" color="text.secondary"><b>Footnote {f.split(':')[0]}:</b>{f.slice(f.indexOf(':') + 1)}</Typography>
        </Box>
      ))}
    </>
  )
}

/** "[^7]" markers rendered as superscripts. */
function renderMarkers(text: string) {
  return text.split(/(\[\^\d+\])/g).map((part, i) => {
    const m = /^\[\^(\d+)\]$/.exec(part)
    return m ? <sup key={i} style={{ color: 'var(--mui-palette-warning-main)', fontWeight: 700 }}>{m[1]}</sup> : <span key={i}>{part}</span>
  })
}

/** Markdown table (as rebuilt at ingestion) rendered as a real table; footnotes below it. */
function MarkdownTable({ text }: { text: string }) {
  const [table, ...footnotes] = text.split('\nFootnote ')
  const rows = table.trim().split('\n')
    .filter(r => r.trim().startsWith('|') && !/^\|\s*-{3}/.test(r.trim()))
    .map(r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim()))
  if (rows.length === 0) return <Typography variant="body2">{text}</Typography>
  const [head, ...body] = rows
  return (
    <>
      <Box sx={{ overflowX: 'auto' }}>
        <Table size="small" sx={{ '& td, & th': { fontSize: 12, py: 0.5, px: 1 } }}>
          <TableHead><TableRow>{head.map((h, i) => <TableCell key={i} sx={{ fontWeight: 700 }}>{renderMarkers(h)}</TableCell>)}</TableRow></TableHead>
          <TableBody>
            {body.map((r, i) => <TableRow key={i}>{r.map((c, j) => <TableCell key={j}>{renderMarkers(c)}</TableCell>)}</TableRow>)}
          </TableBody>
        </Table>
      </Box>
      {footnotes.map(f => (
        <Box key={f} sx={{ mt: 1, pl: 1, borderLeft: 3, borderColor: 'warning.main' }}>
          <Typography variant="caption" color="text.secondary"><b>Footnote {f.split(':')[0]}:</b>{f.slice(f.indexOf(':') + 1)}</Typography>
        </Box>
      ))}
    </>
  )
}

/** Vision description with the rendered page it was written from. */
function Figure({ documentId, page, text, expanded, onToggle }:
  { documentId: number; page: number; text: string; expanded: boolean; onToggle: () => void }) {
  // Vision output often uses Markdown emphasis; show it as plain text.
  text = text.replace(/\*\*/g, '').replace(/^\s*\* /gm, '• ')
  const [failed, setFailed] = useState(false)
  return (
    <>
      {!failed && (
        <Box component="a" href={`/api/documents/${documentId}/pages/${page}.png`} target="_blank" rel="noreferrer"
          sx={{ display: 'block', mb: 1, border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden', height: expanded ? 'auto' : 220 }}>
          {/* Collapsed: show the middle of the page, where figures usually sit, rather than the page title. */}
          <img src={`/api/documents/${documentId}/pages/${page}.png`} alt={`Page ${page}`} onError={() => setFailed(true)}
            style={{ width: '100%', height: expanded ? 'auto' : '100%', objectFit: 'cover', objectPosition: 'center 55%', display: 'block' }} />
        </Box>
      )}
      <Typography variant="body2" sx={{ fontSize: 13, lineHeight: 1.6, whiteSpace: 'pre-wrap' }}>
        {expanded || text.length <= PREVIEW_CHARS ? text : text.slice(0, PREVIEW_CHARS) + '…'}
      </Typography>
      <Button size="small" onClick={onToggle} sx={{ mt: 0.5, px: 0 }}>{expanded ? 'Show less' : 'Show full figure and description'}</Button>
    </>
  )
}
