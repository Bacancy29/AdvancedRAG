import { useQuery } from '@tanstack/react-query'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import Table from '@mui/material/Table'
import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import Typography from '@mui/material/Typography'
import { api } from '../api/client'
import { ProfileChip } from './ProfileChip'

const pct = (x: number) => `${Math.round(x * 100)}%`

/** Naive vs advanced for every document: the harder the document's structure, the bigger the gap. */
export default function Scorecard() {
  const rows = useQuery({ queryKey: ['scorecard'], queryFn: api.scorecard })
  if (!rows.data?.length) return null

  return (
    <Paper sx={{ p: 2.5, mb: 3, overflowX: 'auto' }}>
      <Typography variant="h6">Scorecard: all documents</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
        Latest run per document. Naive = 1,000-character chunks with dense search; Advanced = the full pipeline for that document's profile.
      </Typography>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Document</TableCell>
            <TableCell align="right">MRR naive → advanced</TableCell>
            <TableCell align="right">Hit@5 naive → advanced</TableCell>
            <TableCell align="right">Right section</TableCell>
            <TableCell align="right">Answers passed</TableCell>
            <TableCell>Required</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {rows.data.map(r => (
            <TableRow key={r.documentId}>
              <TableCell>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>{r.name}</Typography>
                <ProfileChip profile={r.profile} />
              </TableCell>
              <TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                {r.naiveMrr.toFixed(2)} → <b>{r.advancedMrr.toFixed(2)}</b>
              </TableCell>
              <TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                {pct(r.naiveHitAt5)} → <b>{pct(r.advancedHitAt5)}</b>
              </TableCell>
              <TableCell align="right">{r.sectionHit == null ? '–' : pct(r.sectionHit)}</TableCell>
              <TableCell align="right">
                {r.total === 0 ? '–' : `${r.passed} / ${r.total}`}
                {r.unanswered > 0 && <Typography variant="caption" color="warning.main" sx={{ display: 'block' }}>{r.unanswered} not answered (API error)</Typography>}
              </TableCell>
              <TableCell>
                {r.total === 0 ? '–' : <Chip size="small" color={r.requiredPassed ? 'success' : 'error'} label={r.requiredPassed ? 'PASS' : 'FAIL'} />}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Paper>
  )
}
