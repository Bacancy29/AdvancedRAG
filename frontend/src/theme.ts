import { createTheme } from '@mui/material/styles'
import type { DocumentProfile } from './api/types'

export const theme = createTheme({
  colorSchemes: { light: true, dark: true },
  cssVariables: { colorSchemeSelector: 'class' },
  shape: { borderRadius: 10 },
  typography: {
    fontFamily: '"Inter", "Segoe UI", system-ui, sans-serif',
    h5: { fontWeight: 700 },
    h6: { fontWeight: 700 },
    button: { textTransform: 'none', fontWeight: 600 },
  },
  components: {
    MuiPaper: { defaultProps: { variant: 'outlined' } },
    MuiChip: { styleOverrides: { root: { fontWeight: 500 } } },
  },
})

/** One colour per pipeline stage, used everywhere a stage appears. */
export const stageColors: Record<string, string> = {
  Ingest: '#64748b',
  Chunk: '#b45309',
  Query: '#7c3aed',
  Retrieve: '#2563eb',
  'Post-process': '#059669',
  Generate: '#ea580c',
}

export const profileLabels: Record<DocumentProfile, string> = {
  PlainText: 'Plain text',
  LongReport: 'Long report',
  Visual: 'Visual',
}

export const profileColors: Record<DocumentProfile, 'primary' | 'secondary' | 'success'> = {
  PlainText: 'primary',
  LongReport: 'secondary',
  Visual: 'success',
}
