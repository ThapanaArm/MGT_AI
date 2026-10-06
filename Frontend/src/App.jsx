import { Navigate, Route, Routes } from 'react-router-dom';
import Layout from './components/Layout';
import ProtectedRoute from './components/ProtectedRoute';
import { useAuth } from './auth/AuthContext';
import LoginPage from './pages/LoginPage';
import ChatPage from './pages/ChatPage';
import ChatLogsPage from './pages/ChatLogsPage';
import AuditLogsPage from './pages/AuditLogsPage';
import CostReportPage from './pages/CostReportPage';
import PolicyRulesPage from './pages/PolicyRulesPage';
import ModelPricingPage from './pages/ModelPricingPage';
import UsersPage from './pages/UsersPage';
import DataSourcesPage from './pages/DataSourcesPage';
import DataSourceGrantsPage from './pages/DataSourceGrantsPage';
import ProjectsPage from './pages/ProjectsPage';
import SkillsPage from './pages/SkillsPage';
import ChangePasswordPage from './pages/ChangePasswordPage';
import SectionLayout from './components/SectionLayout';
import RagCategoriesPage from './pages/RagCategoriesPage';
import RagCreatePage from './pages/RagCreatePage';

const AUDITING_TABS = [
  { to: '/logs/chat', label: 'Search chat logs' },
  { to: '/logs/audit', label: 'System event log' },
  { to: '/logs/cost', label: 'Token cost report' },
];

const ADMIN_TABS = [
  { to: '/admin/policy-rules', label: 'Question screening rules' },
  { to: '/admin/model-pricing', label: 'Model pricing / exchange rate' },
  { to: '/admin/users', label: 'User management' },
  { to: '/admin/data-sources', label: 'Data source registry' },
  { to: '/admin/data-source-access', label: 'Data source access' },
  { to: '/admin/rag-categories', label: 'Setting RAG Category' },
  { to: '/admin/rag-create', label: 'Create RAG' },
];

export default function App() {
  const { checking } = useAuth();

  if (checking) {
    return <div className="center-page">Checking your access…</div>;
  }

  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />

      <Route
        element={
          <ProtectedRoute>
            <Layout />
          </ProtectedRoute>
        }
      >
        <Route path="/" element={<Navigate to="/chat" replace />} />
        <Route path="/chat" element={<ChatPage />} />
        <Route path="/projects" element={<ProjectsPage />} />
        <Route path="/skills" element={<SkillsPage />} />
        <Route path="/change-password" element={<ChangePasswordPage />} />

        <Route
          element={
            <ProtectedRoute roles={['Admin', 'Auditor']}>
              <SectionLayout title="Auditing" tabs={AUDITING_TABS} />
            </ProtectedRoute>
          }
        >
          <Route path="/logs/chat" element={<ChatLogsPage />} />
          <Route path="/logs/audit" element={<AuditLogsPage />} />
          <Route path="/logs/cost" element={<CostReportPage />} />
        </Route>

        <Route
          element={
            <ProtectedRoute roles={['Admin']}>
              <SectionLayout title="Administrator" tabs={ADMIN_TABS} />
            </ProtectedRoute>
          }
        >
          <Route path="/admin/policy-rules" element={<PolicyRulesPage />} />
          <Route path="/admin/model-pricing" element={<ModelPricingPage />} />
          <Route path="/admin/users" element={<UsersPage />} />
          <Route path="/admin/data-sources" element={<DataSourcesPage />} />
          <Route path="/admin/data-source-access" element={<DataSourceGrantsPage />} />
          <Route path="/admin/rag-categories" element={<RagCategoriesPage />} />
          <Route path="/admin/rag-create" element={<RagCreatePage />} />
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/chat" replace />} />
    </Routes>
  );
}
