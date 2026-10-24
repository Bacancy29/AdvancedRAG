import Chip from '@mui/material/Chip'
import type { DocumentProfile, DocumentStatus } from '../api/types'
import { profileColors, profileLabels } from '../theme'

export function ProfileChip({ profile }: { profile: DocumentProfile }) {
  return <Chip size="small" variant="outlined" color={profileColors[profile]} label={profileLabels[profile]} />
}

const statusColor: Record<DocumentStatus, 'default' | 'info' | 'success' | 'error'> = {
  NotIngested: 'default',
  Ingesting: 'info',
  Ready: 'success',
  Failed: 'error',
}

const statusLabel: Record<DocumentStatus, string> = {
  NotIngested: 'Not ingested',
  Ingesting: 'Ingesting…',
  Ready: 'Ready',
  Failed: 'Failed',
}

export function StatusChip({ status }: { status: DocumentStatus }) {
  return <Chip size="small" color={statusColor[status]} label={statusLabel[status]} />
}
