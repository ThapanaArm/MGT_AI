import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';

export default function ChangePasswordPage() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);

    if (newPassword !== confirm) {
      setError('The new password and its confirmation do not match');
      return;
    }

    setBusy(true);
    try {
      await api('/api/auth/change-password', {
        method: 'POST',
        body: { currentPassword, newPassword },
      });
      setDone(true);
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function goToLogin() {
    await logout();
    navigate('/login', { replace: true });
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>Change password</h1>
        <p>
          Current account: {user?.fullName} ({user?.username})
        </p>
      </div>

      {done ? (
        <div className="card narrow">
          <div className="alert alert-ok">
            Password changed. Every signed-in device has been signed out — please sign in again
            with the new password.
          </div>
          <button type="button" className="btn" onClick={goToLogin}>
            Go to sign in
          </button>
        </div>
      ) : (
        <form className="card narrow" onSubmit={handleSubmit}>
          {error && <div className="alert alert-danger">{error}</div>}

          <div className="field">
            <label htmlFor="current">Current password</label>
            <input
              id="current"
              type="password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
          </div>

          <div className="field">
            <label htmlFor="new">New password</label>
            <input
              id="new"
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              autoComplete="new-password"
              minLength={8}
              required
            />
            <div className="faint" style={{ fontSize: 12, marginTop: 4 }}>
              At least 8 characters, containing both letters and digits.
            </div>
          </div>

          <div className="field">
            <label htmlFor="confirm">Confirm new password</label>
            <input
              id="confirm"
              type="password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              autoComplete="new-password"
              required
            />
          </div>

          <button type="submit" className="btn" disabled={busy}>
            {busy ? 'Saving…' : 'Change password'}
          </button>
        </form>
      )}
    </div>
  );
}
