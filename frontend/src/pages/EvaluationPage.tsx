import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import FormControlLabel from '@mui/material/FormControlLabel'
import LinearProgress from '@mui/material/LinearProgress'
import MenuItem from '@mui/material/MenuItem'
import Paper from '@mui/material/Paper'
import Switch from '@mui/material/Switch'
import Table from '@mui/material/Table'
import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import TextField from '@mui/material/TextField'
import Typography from '@mui/material/Typography'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import CancelIcon from '@mui/icons-material/Cancel'
import { api } from '../api/client'
import type { EvalReport } from '../api/types'
import Scorecard from '../components/Scorecard'

export default function EvaluationPage() {
  const { docId } = useParams()
  const navigate = useNavigate()
  const documents = useQuery({ queryKey: ['documents'], queryFn: api.documents })
  const docs = (documents.data ?? []).filter(d => d.hasEvalSet)
  const id = Number(docId) || docs[0]?.id
  const doc = docs.find(d => d.id === id)
  const [withAnswers, setWithAnswers] = useState(true)
  const [report, setReport] = useState<EvalReport | null>(null)

  const latest = useQuery({ queryKey: ['latestEval', id], queryFn: () => api.latestEval(id!), enabled: !!id })
  useEffect(() => { setReport(latest.data ?? null) }, [latest.data])

  const queryClient = useQueryClient()
  const run = useMutation({
    mutationFn: (resume: boolean) => api.runEval(id!, withAnswers, resume),
    onSuccess: r => { setReport(r); queryClient.invalidateQueries({ queryKey: ['scorecard'] }) },
  })

  return (
    <Box sx={{ height: '100%', overflow: 'auto' }}>
      <Box sx={{ maxWidth: 1150, mx: 'auto', p: { xs: 2, md: 3 } }}>
        <Typography variant="h5" sx={{ mb: 0.5 }}>Evaluation</Typography>
        <Typography color="text.secondary" sx={{ mb: 3 }}>
          Hand-checked questions with known answers. The ladder adds one technique per row and measures where the
          answer passage ranks. The answer table checks the final pipeline gives the exact fact, or refuses.
        </Typography>

        <Scorecard />

        <Paper sx={{ p: 2, mb: 3, display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap' }}>
          <TextField select size="small" label="Document" value={id ?? ''} sx={{ minWidth: 280 }}
            onChange={e => navigate(`/evaluation/${e.target.value}`)}>
            {docs.map(d => <MenuItem key={d.id} value={d.id}>{d.name}</MenuItem>)}
          </TextField>
          <FormControlLabel control={<Switch checked={withAnswers} onChange={e => setWithAnswers(e.target.checked)} />}
            label="Also generate answers (uses the chat model)" />
          <Box sx={{ flex: 1 }} />
          <Button variant="contained" disabled={!doc || doc.status !== 'Ready' || run.isPending} onClick={() => run.mutate(false)}>
            {run.isPending ? 'Running…' : 'Run evaluation'}
          </Button>
          {report?.answers.some(a => a.error) && (
            <Button variant="outlined" disabled={run.isPending} onClick={() => run.mutate(true)}>
              Retry {report.answers.filter(a => a.error).length} unanswered only
            </Button>
          )}
        </Paper>
        {run.isPending && <LinearProgress sx={{ mb: 2 }} />}
        {run.error && <Alert severity="error" sx={{ mb: 2 }}>{(run.error as Error).message}</Alert>}
        {doc && doc.status !== 'Ready' && <Alert severity="info">Ingest this document first.</Alert>}
        {!report && !run.isPending && doc?.status === 'Ready' && (
          <Alert severity="info">No evaluation yet. Click <b>Run evaluation</b>.</Alert>
        )}

        {report && <Report report={report} />}
      </Box>
    </Box>
  )
}

function Report({ report }: { report: EvalReport }) {
  const best = Math.max(...report.ladder.map(s => s.mrr), 0.01)
  return (
    <>
      <Typography variant="caption" color="text.secondary">
        Run {new Date(report.createdAt).toLocaleString()} · embeddings {report.embeddingModel} · vector search: {report.vectorSearch ?? "exact cosine in C#"} · answers {report.chatModel}
        · {(report.elapsedMs / 1000).toFixed(0)} s
      </Typography>

      {report.answers.length > 0 && (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, 1fr)' }, gap: 2, my: 2 }}>
          <Stat label="Required questions (pass/fail bar)" value={report.requiredPassed ? 'PASS' : 'FAIL'}
            color={report.requiredPassed ? 'success.main' : 'error.main'} />
          <Stat label="All questions passed" value={`${report.passed} / ${report.total}`} />
          <Stat label="Trap questions refused" value={`${report.answers.filter(a => a.trap && a.passed).length} / ${report.answers.filter(a => a.trap).length}`} />
        </Box>
      )}

      <Paper sx={{ p: 2.5, my: 2 }}>
        <Typography variant="h6">What moved the number</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Mean reciprocal rank (MRR): 1.0 means the answer passage always came first; 0.5 means second on average.
          Hit@5: the answer passage was among the 5 passages the LLM sees. Right section: the top passage came from the
          chapter or region the question is about.
        </Typography>
        {report.ladder.map(s => (
          <Box key={s.name} sx={{ mb: 1.75 }}>
            <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, flexWrap: 'wrap' }}>
              <Typography variant="body2" sx={{ fontWeight: 700, minWidth: 250 }}>{s.name}</Typography>
              <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>{s.description}</Typography>
              <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                MRR <b>{s.mrr.toFixed(2)}</b> · Hit@5 <b>{Math.round(s.hitAt5 * 100)}%</b>
                {s.sectionHit != null && <> · right section <b>{Math.round(s.sectionHit * 100)}%</b></>}
              </Typography>
            </Box>
            <Box sx={{ height: 10, bgcolor: 'action.hover', borderRadius: 5, mt: 0.5, overflow: 'hidden' }}>
              <Box sx={{ width: `${(s.mrr / best) * 100}%`, height: '100%', bgcolor: 'primary.main', borderRadius: 5, transition: 'width .4s' }} />
            </Box>
            <Typography variant="caption" color="text.secondary">
              Rank of the answer passage per question: {s.ranks.map(r => r ?? '–').join(' · ')}
            </Typography>
          </Box>
        ))}
      </Paper>

      {report.answers.length > 0 && (
        <Paper sx={{ my: 2, overflowX: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Result</TableCell>
                <TableCell>Question</TableCell>
                <TableCell>Expected</TableCell>
                <TableCell>Answer</TableCell>
                <TableCell>Pages</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {report.answers.map(a => (
                <TableRow key={a.id} sx={{ verticalAlign: 'top' }}>
                  <TableCell>
                    {a.passed ? <CheckCircleIcon color="success" fontSize="small" /> : <CancelIcon color="error" fontSize="small" />}
                  </TableCell>
                  <TableCell sx={{ minWidth: 220 }}>
                    <Box sx={{ display: 'flex', gap: 0.5, mb: 0.5, flexWrap: 'wrap' }}>
                      <Chip size="small" label={a.id} />
                      {a.required && <Chip size="small" color="primary" label="Required" />}
                      {a.trap && <Chip size="small" color="warning" label="Trap" />}
                    </Box>
                    <Typography variant="body2">{a.question}</Typography>
                    <Typography variant="caption" color="text.secondary">{a.category}</Typography>
                  </TableCell>
                  <TableCell sx={{ minWidth: 160 }}><Typography variant="body2">{a.expected}</Typography></TableCell>
                  <TableCell sx={{ minWidth: 280 }}>
                    <Typography variant="body2" color={a.error ? 'error' : 'text.primary'}>{a.error ?? a.answer}</Typography>
                    {a.failReason && <Typography variant="caption" color="error" sx={{ display: 'block', mt: 0.5 }}>{a.failReason}</Typography>}
                    {a.topSection && <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>Top source: {a.topSection.split(' > ').join(' › ')}</Typography>}
                  </TableCell>
                  <TableCell>{a.citedPages.join(', ')}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Paper>
      )}
    </>
  )
}

function Stat({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <Paper sx={{ p: 2 }}>
      <Typography variant="caption" color="text.secondary">{label}</Typography>
      <Typography variant="h4" sx={{ fontWeight: 800, color, fontVariantNumeric: 'tabular-nums' }}>{value}</Typography>
    </Paper>
  )
}
