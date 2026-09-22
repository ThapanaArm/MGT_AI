import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
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

const COLLAPSE_STORAGE_KEY = 'sidebarCollapsed';

export default function Layout() {
  const { user, role, logout } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [showUserMenu, setShowUserMenu] = useState(false);
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(COLLAPSE_STORAGE_KEY) === '1';
    } catch {
      return false;
    }
  });
  // Narrow-viewport sidebar becomes an off-canvas drawer instead of collapsing — this tracks
  // whether it's currently slid open, independent of the desktop collapse preference above.
  const [mobileOpen, setMobileOpen] = useState(false);
  // Callback ref (not useRef) — its DOM node only exists after Layout's own commit, and a
  // portal target must be a real node before ChatPage (rendered via Outlet) can portal into it.
  const [historySlot, setHistorySlot] = useState(null);

  // Close the mobile drawer whenever the route changes (e.g. a nav link or a recent-chat click).
  useEffect(() => {
    setMobileOpen(false);
  }, [location.pathname, location.search]);

  async function handleLogout() {
    await logout();
    navigate('/login', { replace: true });
  }

  function toggleCollapsed() {
    setCollapsed((prev) => {
      const next = !prev;
      try {
        localStorage.setItem(COLLAPSE_STORAGE_KEY, next ? '1' : '0');
      } catch {
        // Private browsing or storage disabled — the preference just won't persist.
      }
      return next;
    });
  }

  return (
    <div className="app-shell">
      <button
        type="button"
        className="sidebar-mobile-toggle"
        onClick={() => setMobileOpen(true)}
        aria-label="Open menu"
      >
        ☰
      </button>

      <div
        className={`sidebar-backdrop${mobileOpen ? ' visible' : ''}`}
        onClick={() => setMobileOpen(false)}
        aria-hidden="true"
      />

      <nav className={`sidebar${collapsed ? ' collapsed' : ''}${mobileOpen ? ' mobile-open' : ''}`}>
        <button
          type="button"
          className="sidebar-collapse-btn"
          onClick={toggleCollapsed}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          {collapsed ? '›' : '‹'}
        </button>

        <button
          type="button"
          className="sidebar-mobile-close"
          onClick={() => setMobileOpen(false)}
          aria-label="Close menu"
        >
          ×
        </button>

        <div className="sidebar-brand">
          <span className="sidebar-brand-icon" aria-hidden="true">✨</span>
          <span className="sidebar-brand-text">
            MGT AI Assistant
            <small>Your workplace AI assistant</small>
          </span>
        </div>

        {/* A changing query value guarantees a location change even when already on /chat,
            so ChatPage's effect (which watches for ?new=) always fires and resets the view —
            a plain "/chat" link would be a no-op click while already on that exact route. */}
        <NavLink to={`/chat?new=${Date.now()}`} className="btn sidebar-new-chat" title="New chat">
          <span aria-hidden="true">+</span> <span className="nav-label">New chat</span>
        </NavLink>

        <NavLink to="/chat" className="nav-link" end title="Chat with AI">
          <span className="nav-icon" aria-hidden="true">💬</span> <span className="nav-label">Chat with AI</span>
        </NavLink>
        <NavLink to="/projects" className="nav-link" title="Projects">
          <span className="nav-icon" aria-hidden="true">📁</span> <span className="nav-label">Projects</span>
        </NavLink>
        <NavLink to="/skills" className="nav-link" title="Prompt library">
          <span className="nav-icon" aria-hidden="true">📚</span> <span className="nav-label">Prompt library</span>
        </NavLink>

        {/* ChatPage portals its search box + recent-conversations list in here, so it reads
            as one continuous panel with the nav above it, per the mockup. Empty on other pages. */}
        <div className="sidebar-history-slot" ref={setHistorySlot} />

        <div className="sidebar-footer">
          <NavLink to="/chat" className="nav-link sidebar-history" title="View your recent conversations">
            <span className="nav-icon" aria-hidden="true">🕐</span> <span className="nav-label">Chat history</span>
          </NavLink>

          <button
            type="button"
            className="sidebar-user-row"
            onClick={() => setShowUserMenu((v) => !v)}
            aria-expanded={showUserMenu}
            title={user?.fullName}
          >
            <span className="sidebar-avatar" aria-hidden="true">{initialsOf(user?.fullName)}</span>
            <span className="sidebar-user nav-label">{user?.fullName}</span>
            <span className={`sidebar-user-chevron nav-label${showUserMenu ? ' open' : ''}`} aria-hidden="true">▾</span>
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
        <Outlet context={{ historySlot, closeMobileSidebar: () => setMobileOpen(false) }} />
      </div>
    </div>
  );
}
