import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import AppBar from '@mui/material/AppBar'
import Box from '@mui/material/Box'
import Chip from '@mui/material/Chip'
import Tab from '@mui/material/Tab'
import Tabs from '@mui/material/Tabs'
import Toolbar from '@mui/material/Toolbar'
import Tooltip from '@mui/material/Tooltip'
import Typography from '@mui/material/Typography'
import AutoStoriesIcon from '@mui/icons-material/AutoStories'
import { api } from './api/client'
import ChatPage from './pages/ChatPage'
import DocumentsPage from './pages/DocumentsPage'
import EvaluationPage from './pages/EvaluationPage'
import HowItWorksPage from './pages/HowItWorksPage'

import { SHOW_DEV_VIEWS } from './devViews'

const tabs = [
  { path: '/chat', label: 'Chat' },
  { path: '/documents', label: 'Documents' },
  ...(SHOW_DEV_VIEWS ? [
    { path: '/evaluation', label: 'Evaluation' },
    { path: '/how-it-works', label: 'How it works' },
  ] : []),
]

export default function App() {
  const location = useLocation()
  const navigate = useNavigate()
  const health = useQuery({ queryKey: ['health'], queryFn: api.health })
  const current = tabs.find(t => location.pathname.startsWith(t.path))?.path ?? false

  return (
    <Box sx={{ height: '100vh', display: 'flex', flexDirection: 'column' }}>
      <AppBar position="static" color="inherit" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider' }}>
        <Toolbar sx={{ gap: 2, minHeight: { xs: 56 } }}>
          <AutoStoriesIcon color="primary" />
          <Typography variant="h6" sx={{ whiteSpace: 'nowrap' }}>Advanced RAG</Typography>
          <Tabs value={current} onChange={(_, v) => navigate(v)} sx={{ flex: 1, minWidth: 0 }} variant="scrollable">
            {tabs.map(t => <Tab key={t.path} value={t.path} label={t.label} />)}
          </Tabs>
          {SHOW_DEV_VIEWS && health.data && (
            <Box sx={{ display: { xs: 'none', md: 'flex' }, gap: 1 }}>
              <Tooltip title="Chat model (answers)">
                <Chip size="small" variant="outlined" label={health.data.chatModel}
                  color={health.data.chatConfigured ? 'default' : 'error'} />
              </Tooltip>
              <Tooltip title={health.data.vectorSearch}>
                <Chip size="small" variant="outlined" label={health.data.database} />
              </Tooltip>
              <Tooltip title="Embedding model">
                <Chip size="small" variant="outlined" label={health.data.embeddingModel} />
              </Tooltip>
              <Tooltip title="Cross-encoder reranker">
                <Chip size="small" variant="outlined" label={health.data.reranker ? 'Reranker on' : 'Reranker missing'}
                  color={health.data.reranker ? 'default' : 'warning'} />
              </Tooltip>
            </Box>
          )}
        </Toolbar>
      </AppBar>
      {health.data && !health.data.chatConfigured && (
        <Box sx={{ px: 2, py: 1, bgcolor: 'error.main', color: 'error.contrastText', fontSize: 14 }}>
          No API key for {health.data.chatModel}. Run
          <code style={{ margin: '0 6px' }}>dotnet user-secrets set "AI:Chat:ApiKey" "&lt;key&gt;" --id advanced-rag</code>
          and restart the API.
        </Box>
      )}
      {health.isError && (
        <Box sx={{ px: 2, py: 1, bgcolor: 'error.main', color: 'error.contrastText', fontSize: 14 }}>
          Cannot reach the API on http://localhost:5080. Start it with <code>dotnet run --project Rag.Api</code>.
        </Box>
      )}

      <Box sx={{ flex: 1, minHeight: 0 }}>
        <Routes>
          <Route path="/" element={<Navigate to="/chat" replace />} />
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/chat/:docId" element={<ChatPage />} />
          <Route path="/documents" element={<DocumentsPage />} />
          <Route path="/evaluation" element={<EvaluationPage />} />
          <Route path="/evaluation/:docId" element={<EvaluationPage />} />
          <Route path="/how-it-works" element={<HowItWorksPage />} />
        </Routes>
      </Box>
    </Box>
  )
}
