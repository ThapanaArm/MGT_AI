import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

const ROLE_LABELS = {
  Admin: 'Administrator',
  Auditor: 'Auditor',
  User: 'Employee',
};

export default function Layout() {
  const { user, role, canReadLogs, isAdmin, logout } = useAuth();
  const navigate = useNavigate();

  async function handleLogout() {
    await logout();
    navigate('/login', { replace: true });
  }

  return (
    <div className="app-shell">
      <nav className="sidebar">
        <div className="sidebar-brand">
          MGT AI Assistant
          <small>Internal AI assistant</small>
        </div>

        <NavLink to="/chat" className="nav-link">
          Chat with AI
        </NavLink>
        <NavLink to="/projects" className="nav-link">
          Projects
        </NavLink>
        <NavLink to="/skills" className="nav-link">
          Skills
        </NavLink>

        {canReadLogs && (
          <>
            <div className="sidebar-section">Auditing</div>
            <NavLink to="/logs/chat" className="nav-link">
              Search chat logs
            </NavLink>
            <NavLink to="/logs/audit" className="nav-link">
              System event log
            </NavLink>
            <NavLink to="/logs/cost" className="nav-link">
              Token cost report
            </NavLink>
          </>
        )}

        {isAdmin && (
          <>
            <div className="sidebar-section">Administration</div>
            <NavLink to="/admin/policy-rules" className="nav-link">
              Question screening rules
            </NavLink>
            <NavLink to="/admin/model-pricing" className="nav-link">
              Model pricing / exchange rate
            </NavLink>
            <NavLink to="/admin/users" className="nav-link">
              User management
            </NavLink>
            <NavLink to="/admin/data-sources" className="nav-link">
              Data source registry
            </NavLink>
            <NavLink to="/admin/data-source-access" className="nav-link">
              Data source access
            </NavLink>
          </>
        )}

        <div className="sidebar-footer">
          <div className="sidebar-user">{user?.fullName}</div>
          <div className="sidebar-meta">
            {user?.username} · {ROLE_LABELS[role] ?? role}
            {user?.department ? ` · ${user.department}` : ''}
          </div>
          <div className="row-actions">
            <NavLink to="/change-password" className="btn btn-secondary btn-sm">
              Change password
            </NavLink>
            <button type="button" className="btn btn-secondary btn-sm" onClick={handleLogout}>
              Sign out
            </button>
          </div>
        </div>
      </nav>

      <div className="main">
        <Outlet />
      </div>
    </div>
  );
}
