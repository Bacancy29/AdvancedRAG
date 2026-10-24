import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import Box from '@mui/material/Box'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import ToggleButton from '@mui/material/ToggleButton'
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup'
import Tooltip from '@mui/material/Tooltip'
import Typography from '@mui/material/Typography'
import { api } from '../api/client'
import type { DocumentProfile } from '../api/types'
import { profileLabels, stageColors } from '../theme'

/** The six-stage technique map. Picking a profile highlights the techniques that run for it. */
export default function HowItWorksPage() {
  const pipeline = useQuery({ queryKey: ['pipeline'], queryFn: api.pipeline })
  const [profile, setProfile] = useState<DocumentProfile | 'All'>('PlainText')

  return (
    <Box sx={{ height: '100%', overflow: 'auto' }}>
      <Box sx={{ maxWidth: 1300, mx: 'auto', p: { xs: 2, md: 3 } }}>
        <Typography variant="h5" sx={{ mb: 0.5 }}>Basic RAG has three steps. Advanced RAG improves each one.</Typography>
        <Typography color="text.secondary" sx={{ mb: 2.5 }}>
          Two stages run once per document (offline), four run on every question. Each card is one technique.
          Pick a document type to see which techniques it uses.
        </Typography>

        <ToggleButtonGroup exclusive size="small" value={profile} onChange={(_, v) => v && setProfile(v)} sx={{ mb: 3 }}>
          <ToggleButton value="All">All</ToggleButton>
          {(Object.keys(profileLabels) as DocumentProfile[]).map(p => (
            <ToggleButton key={p} value={p}>{profileLabels[p]}</ToggleButton>
          ))}
        </ToggleButtonGroup>

        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', lg: 'repeat(6, 1fr)' }, gap: 2 }}>
          {pipeline.data?.map(stage => (
            <Box key={stage.name}>
              <Box sx={{ borderBottom: 3, borderColor: stageColors[stage.name], pb: 0.5, mb: 1 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>
                  <Typography component="span" color="text.secondary" sx={{ mr: 0.75 }}>{stage.number}</Typography>
                  {stage.name}
                </Typography>
              </Box>
              <Typography variant="overline" color="text.secondary">{stage.when}</Typography>
              {stage.techniques.map(t => {
                const active = profile === 'All' || t.profiles.includes(profile)
                return (
                  <Tooltip key={t.name} title={t.description} placement="top">
                    <Paper sx={{ p: 1.25, mb: 1, opacity: active ? 1 : 0.35 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.75 }}>{t.name}</Typography>
                      <Box sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap' }}>
                        {t.profiles.map(p => <Chip key={p} size="small" label={profileLabels[p]} sx={{ height: 20, fontSize: 11 }} />)}
                        {!t.implemented && <Chip size="small" color="secondary" variant="outlined" label="next" sx={{ height: 20, fontSize: 11 }} />}
                      </Box>
                    </Paper>
                  </Tooltip>
                )
              })}
            </Box>
          ))}
        </Box>
      </Box>
    </Box>
  )
}
