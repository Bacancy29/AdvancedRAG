import { useNavigate } from 'react-router-dom'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import List from '@mui/material/List'
import ListItemButton from '@mui/material/ListItemButton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import AddIcon from '@mui/icons-material/Add'
import type { RagDocument } from '../api/types'
import { ProfileChip, StatusChip } from './ProfileChip'

interface Props {
  documents: RagDocument[]
  selectedId: number | null
  onSelect: (id: number) => void
}

export default function DocumentSidebar({ documents, selectedId, onSelect }: Props) {
  const navigate = useNavigate()
  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderRight: 1, borderColor: 'divider' }}>
      <Typography variant="overline" sx={{ px: 2, pt: 1.5, color: 'text.secondary' }}>Documents</Typography>
      <List sx={{ flex: 1, overflow: 'auto', py: 0 }}>
        {documents.map(d => (
          <ListItemButton key={d.id} selected={d.id === selectedId} onClick={() => onSelect(d.id)}
            sx={{ display: 'block', py: 1.25 }}>
            <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.75 }}>{d.name}</Typography>
            <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', rowGap: 0.75 }}>
              <ProfileChip profile={d.profile} />
              <StatusChip status={d.status} />
            </Stack>
            {d.status === 'Ready' && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                {d.pageCount} pages · {d.chunkCount} chunks
              </Typography>
            )}
          </ListItemButton>
        ))}
      </List>
      <Box sx={{ p: 1.5, borderTop: 1, borderColor: 'divider' }}>
        <Button fullWidth variant="outlined" startIcon={<AddIcon />} onClick={() => navigate('/documents')}>
          Add document
        </Button>
      </Box>
    </Box>
  )
}
