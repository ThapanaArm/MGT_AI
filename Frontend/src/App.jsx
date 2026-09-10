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
          path="/logs/chat"
          element={
            <ProtectedRoute roles={['Admin', 'Auditor']}>
              <ChatLogsPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/logs/audit"
          element={
            <ProtectedRoute roles={['Admin', 'Auditor']}>
              <AuditLogsPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/logs/cost"
          element={
            <ProtectedRoute roles={['Admin', 'Auditor']}>
              <CostReportPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/admin/policy-rules"
          element={
            <ProtectedRoute roles={['Admin']}>
              <PolicyRulesPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/admin/model-pricing"
          element={
            <ProtectedRoute roles={['Admin']}>
              <ModelPricingPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/admin/users"
          element={
            <ProtectedRoute roles={['Admin']}>
              <UsersPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/admin/data-sources"
          element={
            <ProtectedRoute roles={['Admin']}>
              <DataSourcesPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/admin/data-source-access"
          element={
            <ProtectedRoute roles={['Admin']}>
              <DataSourceGrantsPage />
            </ProtectedRoute>
          }
        />
      </Route>

      <Route path="*" element={<Navigate to="/chat" replace />} />
    </Routes>
  );
}
