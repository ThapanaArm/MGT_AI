import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

const ROLE_LABELS = {
  Admin: 'Administrator',
  Auditor: 'Auditor',
  User: 'Employee',
};

/** Initials for the avatar circle — "Somchai Jaidee" -> "SJ", a single name -> its first letter. */
function initialsOf(fullName) {
  if (!fullName) return '?';
  const parts = fullName.trim().split(/\s+/);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
}

export default function Layout() {
  const { user, role, canReadLogs, isAdmin, logout } = useAuth();
  const navigate = useNavigate();
  const [showUserMenu, setShowUserMenu] = useState(false);

  async function handleLogout() {
    await logout();
    navigate('/login', { replace: true });
  }

  return (
    <div className="app-shell">
      <nav className="sidebar">
        <div className="sidebar-brand">
          <span className="sidebar-brand-icon" aria-hidden="true">✨</span>
          <span>
            MGT AI Assistant
            <small>Your workplace AI assistant</small>
          </span>
        </div>

        {/* A changing query value guarantees a location change even when already on /chat,
            so ChatPage's effect (which watches for ?new=) always fires and resets the view —
            a plain "/chat" link would be a no-op click while already on that exact route. */}
        <NavLink to={`/chat?new=${Date.now()}`} className="btn sidebar-new-chat">
          + New chat
        </NavLink>

        <NavLink to="/chat" className="nav-link" end>
          <span className="nav-icon" aria-hidden="true">💬</span> Chat with AI
        </NavLink>
        <NavLink to="/projects" className="nav-link">
          <span className="nav-icon" aria-hidden="true">📁</span> Projects
        </NavLink>
        <NavLink to="/skills" className="nav-link">
          <span className="nav-icon" aria-hidden="true">📚</span> Prompt library
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
          <button
            type="button"
            className="nav-link sidebar-help"
            title="Contact your IT administrator for help with this assistant"
          >
            <span className="nav-icon" aria-hidden="true">❓</span> Help center
          </button>

          <button
            type="button"
            className="sidebar-user-row"
            onClick={() => setShowUserMenu((v) => !v)}
            aria-expanded={showUserMenu}
          >
            <span className="sidebar-avatar" aria-hidden="true">{initialsOf(user?.fullName)}</span>
            <span className="sidebar-user">{user?.fullName}</span>
            <span className={`sidebar-user-chevron${showUserMenu ? ' open' : ''}`} aria-hidden="true">▾</span>
          </button>

          {showUserMenu && (
            <div className="sidebar-user-menu">
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
          )}
        </div>
      </nav>

      <div className="main">
        <Outlet />
      </div>
    </div>
  );
}
