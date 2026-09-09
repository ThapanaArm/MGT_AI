import { useState } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { landingPath } from '../lib/routes';

const TEST_USERS = [
  { username: 'admin01', password: 'Admin@2026', role: 'Administrator — chat, all logs, full system management' },
  { username: 'somchai', password: 'Somchai@2026', role: 'Employee — chat only' },
  { username: 'sunee', password: 'Sunee@2026', role: 'Auditor — read logs, no system changes' },
];

export default function LoginPage() {
  const { isAuthenticated, role, login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  if (isAuthenticated) {
    return <Navigate to={landingPath(role, location.state?.from)} replace />;
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setBusy(true);

    try {
      const profile = await login(username.trim(), password);
      navigate(landingPath(profile.userRole, location.state?.from), { replace: true });
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  function fill(user) {
    setUsername(user.username);
    setPassword(user.password);
    setError(null);
  }

  return (
    <div className="login-page">
      <form className="login-card" onSubmit={handleSubmit}>
        <h1>Sign in</h1>
        <p className="subtitle">
          Internal AI assistant. You must authenticate before use, and every conversation is
          logged for auditing.
        </p>

        {error && <div className="alert alert-danger">{error}</div>}

        <div className="field">
          <label htmlFor="username">Username</label>
          <input
            id="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoFocus
            required
          />
        </div>

        <div className="field">
          <label htmlFor="password">Password</label>
          <input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </div>

        <button type="submit" className="btn" disabled={busy || !username || !password}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <div className="test-users">
          <strong>Test accounts</strong> (click to fill in)
          <table>
            <tbody>
              {TEST_USERS.map((user) => (
                <tr key={user.username}>
                  <td>
                    <button type="button" className="btn-link" onClick={() => fill(user)}>
                      <code>{user.username}</code>
                    </button>
                  </td>
                  <td>
                    <code>{user.password}</code>
                  </td>
                  <td>{user.role}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </form>
    </div>
  );
}
