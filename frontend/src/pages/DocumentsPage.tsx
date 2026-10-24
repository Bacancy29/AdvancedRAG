import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import Dialog from '@mui/material/Dialog'
import DialogContent from '@mui/material/DialogContent'
import DialogTitle from '@mui/material/DialogTitle'
import IconButton from '@mui/material/IconButton'
import LinearProgress from '@mui/material/LinearProgress'
import MenuItem from '@mui/material/MenuItem'
import Paper from '@mui/material/Paper'
import Stack from '@mui/material/Stack'
import TextField from '@mui/material/TextField'
import ToggleButton from '@mui/material/ToggleButton'
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup'
import Typography from '@mui/material/Typography'
import CloseIcon from '@mui/icons-material/Close'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import UploadFileIcon from '@mui/icons-material/UploadFile'
import { api } from '../api/client'
import type { DocumentProfile, RagDocument } from '../api/types'
import { ProfileChip, StatusChip } from '../components/ProfileChip'
import { profileLabels } from '../theme'
import { SHOW_DEV_VIEWS } from '../devViews'

const profileHelp: Record<DocumentProfile, string> = {
  PlainText: 'Narrative text, numbers inside sentences (use case 1)',
  LongReport: 'Long, structured: chapters, sections, boxes (use case 2)',
  Visual: 'Tables, charts and footnotes (use case 3)',
}
const availableProfiles: DocumentProfile[] = ['PlainText', 'LongReport', 'Visual']

export default function DocumentsPage() {
  const queryClient = useQueryClient()
  const documents = useQuery({
    queryKey: ['documents'],
    queryFn: api.documents,
    // Poll while something is ingesting so the progress bar moves.
    refetchInterval: q => (q.state.data?.some(isBusy) ? 1500 : false),
  })
  const [chunksFor, setChunksFor] = useState<RagDocument | null>(null)

  const ingest = useMutation({
    mutationFn: api.ingest,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['documents'] }),
  })
  const remove = useMutation({
    mutationFn: api.deleteDocument,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['documents'] }),
  })

  return (
    <Box sx={{ height: '100%', overflow: 'auto' }}>
      <Box sx={{ maxWidth: 1100, mx: 'auto', p: { xs: 2, md: 3 } }}>
        <Typography variant="h5" sx={{ mb: 0.5 }}>Documents</Typography>
        <Typography color="text.secondary" sx={{ mb: 3 }}>
          Each document has a profile. The profile decides which pipeline techniques run on it.
        </Typography>

        {ingest.error && <Alert severity="error" sx={{ mb: 2 }}>{(ingest.error as Error).message}</Alert>}

        <Stack spacing={1.5} sx={{ mb: 4 }}>
          {documents.data?.map(d => (
            <Paper key={d.id} sx={{ p: 2 }}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
                <Box sx={{ flex: 1, minWidth: 220 }}>
                  <Typography sx={{ fontWeight: 700 }}>{d.name}</Typography>
                  <Typography variant="caption" color="text.secondary">
                    {d.fileName}
                    {d.status === 'Ready' && ` · ${d.pageCount} pages · ${d.chunkCount} chunks · ${d.embeddingModel}`}
                  </Typography>
                </Box>
                <ProfileChip profile={d.profile} />
                <StatusChip status={isBusy(d) ? 'Ingesting' : d.status} />
                <Button size="small" variant={d.status === 'Ready' ? 'outlined' : 'contained'}
                  disabled={isBusy(d)} onClick={() => ingest.mutate(d.id)}>
                  {d.status === 'Ready' ? 'Re-ingest' : 'Ingest'}
                </Button>
                <Button size="small" disabled={d.status !== 'Ready'} onClick={() => setChunksFor(d)}>View chunks</Button>
                <IconButton size="small" disabled={isBusy(d)}
                  onClick={() => confirm(`Delete "${d.name}" and its chunks?`) && remove.mutate(d.id)}>
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Box>
              {d.progress && (isBusy(d) || d.progress.stage === 'Failed') && (
                <Box sx={{ mt: 1.5 }}>
                  <LinearProgress variant="determinate" value={d.progress.percent}
                    color={d.progress.stage === 'Failed' ? 'error' : 'primary'} />
                  <Typography variant="caption" color="text.secondary">{d.progress.stage}: {d.progress.message}</Typography>
                </Box>
              )}
              {d.status === 'Failed' && d.error && !isBusy(d) && <Alert severity="error" sx={{ mt: 1.5 }}>{d.error}</Alert>}
            </Paper>
          ))}
        </Stack>

        <UploadCard onUploaded={() => queryClient.invalidateQueries({ queryKey: ['documents'] })} />
      </Box>
      {chunksFor && <ChunksDialog document={chunksFor} onClose={() => setChunksFor(null)} />}
    </Box>
  )
}

function isBusy(d: RagDocument) {
  return d.status === 'Ingesting' || (d.progress != null && d.progress.percent < 100 && d.progress.stage !== 'Failed')
}

function UploadCard({ onUploaded }: { onUploaded: () => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [name, setName] = useState('')
  const [profile, setProfile] = useState<DocumentProfile>('PlainText')
  const upload = useMutation({
    mutationFn: () => api.upload(file!, name, profile),
    onSuccess: () => { setFile(null); setName(''); onUploaded() },
  })

  return (
    <Paper sx={{ p: 2.5 }}>
      <Typography variant="h6" sx={{ mb: 2 }}>Add a document</Typography>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ alignItems: { md: 'flex-start' } }}>
        <Button component="label" variant="outlined" startIcon={<UploadFileIcon />} sx={{ whiteSpace: 'nowrap', py: 1 }}>
          {file ? file.name : 'Choose PDF'}
          <input hidden type="file" accept="application/pdf" onChange={e => {
            const f = e.target.files?.[0] ?? null
            setFile(f)
            if (f && !name) setName(f.name.replace(/\.pdf$/i, ''))
          }} />
        </Button>
        <TextField size="small" label="Name" value={name} onChange={e => setName(e.target.value)} sx={{ flex: 1 }} />
        <TextField select size="small" label="Profile" value={profile} sx={{ minWidth: 260 }}
          onChange={e => setProfile(e.target.value as DocumentProfile)}
          helperText={profileHelp[profile]}>
          {(Object.keys(profileLabels) as DocumentProfile[]).map(p => (
            <MenuItem key={p} value={p} disabled={!availableProfiles.includes(p)}>
              {profileLabels[p]}{!availableProfiles.includes(p) && ' (coming soon)'}
            </MenuItem>
          ))}
        </TextField>
        <Button variant="contained" disabled={!file || upload.isPending} onClick={() => upload.mutate()} sx={{ py: 1 }}>
          Upload
        </Button>
      </Stack>
      {upload.error && <Alert severity="error" sx={{ mt: 2 }}>{(upload.error as Error).message}</Alert>}
    </Paper>
  )
}

function ChunksDialog({ document, onClose }: { document: RagDocument; onClose: () => void }) {
  const [set, setSet] = useState<'advanced' | 'naive'>('advanced')
  const [filter, setFilter] = useState('')
  const chunks = useQuery({ queryKey: ['chunks', document.id, set], queryFn: () => api.chunks(document.id, set) })
  const shown = useMemo(() => {
    const f = filter.trim().toLowerCase()
    return (chunks.data ?? []).filter(c => !f || c.text.toLowerCase().includes(f) || c.sectionPath.toLowerCase().includes(f))
  }, [chunks.data, filter])

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md" scroll="paper">
      <DialogTitle sx={{ display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
        Chunks: {document.name}
        <Box sx={{ flex: 1 }} />
        <IconButton onClick={onClose}><CloseIcon /></IconButton>
      </DialogTitle>
      <Box sx={{ px: 3, pb: 1.5, display: 'flex', gap: 2, flexWrap: 'wrap', alignItems: 'center' }}>
        {SHOW_DEV_VIEWS && (
          <ToggleButtonGroup size="small" exclusive value={set} onChange={(_, v) => v && setSet(v)}>
            <ToggleButton value="advanced">Advanced (sentence chunks)</ToggleButton>
            <ToggleButton value="naive">Naive (1,000 characters)</ToggleButton>
          </ToggleButtonGroup>
        )}
        <TextField size="small" placeholder="Filter text…" value={filter} onChange={e => setFilter(e.target.value)} sx={{ flex: 1, minWidth: 200 }} />
        <Typography variant="caption" color="text.secondary">{shown.length} chunks</Typography>
      </Box>
      <DialogContent dividers>
        {chunks.isLoading && <LinearProgress />}
        {shown.map(c => (
          <Paper key={c.id} sx={{ p: 1.5, mb: 1 }}>
            <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', mb: 0.5, flexWrap: 'wrap' }}>
              <Typography variant="caption" sx={{ fontWeight: 700 }}>#{c.chunkIndex}</Typography>
              <Typography variant="caption">p.{c.pageNumber}{c.pageEnd !== c.pageNumber && `–${c.pageEnd}`}</Typography>
              <Chip size="small" variant="outlined" label={c.contentType} />
              <Typography variant="caption" color="text.secondary">{c.tokenCount} tokens</Typography>
              {c.sectionPath && <Typography variant="caption" color="text.secondary" noWrap sx={{ ml: 'auto', maxWidth: 360 }}>
                {c.sectionPath.split(' > ').join(' › ')}
              </Typography>}
            </Box>
            <Typography variant="body2" sx={{ fontSize: 13 }}>{c.text}</Typography>
          </Paper>
        ))}
      </DialogContent>
    </Dialog>
  )
}
