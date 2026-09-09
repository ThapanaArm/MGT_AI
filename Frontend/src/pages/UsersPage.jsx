import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { USER_ROLES, formatDateTime, formatNumber } from '../lib/constants';
import { useAuth } from '../auth/AuthContext';

const EMPTY_CREATE = {
  username: '',
  password: '',
  email: '',
  fullName: '',
  department: '',
  userRole: 'User',
  mustChangePassword: true,
};

export default function UsersPage() {
  const { user: currentUser } = useAuth();

  const [users, setUsers] = useState([]);
  const [createForm, setCreateForm] = useState(EMPTY_CREATE);
  const [editing, setEditing] = useState(null);
  const [resetting, setResetting] = useState(null);
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setUsers(await api('/api/admin/users'));
    } catch (err) {
      setError(err.message);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function handleCreate(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      await api('/api/admin/users', { method: 'POST', body: createForm });
      setMessage(`Created the user ${createForm.username}`);
      setCreateForm(EMPTY_CREATE);
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleUpdate(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      await api(`/api/admin/users/${editing.userId}`, {
        method: 'PUT',
        body: {
          email: editing.email,
          fullName: editing.fullName,
          department: editing.department ?? '',
          userRole: editing.userRole,
          isActive: editing.isActive,
        },
      });
      setMessage(`Saved changes for ${editing.username}`);
      setEditing(null);
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleReset(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      await api(`/api/admin/users/${resetting.userId}/reset-password`, {
        method: 'POST',
        body: { newPassword, mustChangePassword: true },
      });
      setMessage(
        `Password reset for ${resetting.username} — all of their existing sessions were signed out`,
      );
      setResetting(null);
      setNewPassword('');
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>User management</h1>
        <p>Control who can sign in and at what access level — every change is recorded in the audit log.</p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">All users ({users.length})</div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="num">#</th>
                <th>Username</th>
                <th>Full name</th>
                <th className="nowrap">Department</th>
                <th className="nowrap">Role</th>
                <th className="nowrap">Status</th>
                <th className="num nowrap">Questions asked</th>
                <th className="nowrap">Last sign-in</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.userId}>
                  <td className="num">{user.userId}</td>
                  <td>
                    {user.username}
                    {user.userId === currentUser?.userId && (
                      <span className="badge badge-info" style={{ marginLeft: 6 }}>
                        you
                      </span>
                    )}
                    <div className="faint">{user.email}</div>
                  </td>
                  <td>{user.fullName}</td>
                  <td className="nowrap">{user.department ?? '—'}</td>
                  <td className="nowrap">
                    <span className={user.userRole === 'Admin' ? 'badge badge-brand' : 'badge'}>
                      {user.userRole}
                    </span>
                  </td>
                  <td className="nowrap">
                    <span className={user.isActive ? 'badge badge-ok' : 'badge badge-danger'}>
                      {user.isActive ? 'Active' : 'Disabled'}
                    </span>
                    {user.lockoutUntil && new Date(user.lockoutUntil) > new Date() && (
                      <div className="badge badge-warn" style={{ marginTop: 3 }}>
                        Locked until {formatDateTime(user.lockoutUntil)}
                      </div>
                    )}
                  </td>
                  <td className="num">{formatNumber(user.messageCount)}</td>
                  <td className="nowrap faint">{formatDateTime(user.lastLoginAt)}</td>
                  <td className="nowrap">
                    <div className="row-actions">
                      <button
                        type="button"
                        className="btn btn-secondary btn-sm"
                        onClick={() => {
                          setEditing({ ...user });
                          setResetting(null);
                        }}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className="btn btn-secondary btn-sm"
                        onClick={() => {
                          setResetting(user);
                          setEditing(null);
                          setNewPassword('');
                        }}
                      >
                        Reset password
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {editing && (
        <form className="card narrow" onSubmit={handleUpdate}>
          <div className="card-title">Edit user: {editing.username}</div>

          <div className="form-grid">
            <div className="field">
              <label htmlFor="edit-fullName">Full name</label>
              <input
                id="edit-fullName"
                value={editing.fullName}
                onChange={(e) => setEditing({ ...editing, fullName: e.target.value })}
                required
              />
            </div>

            <div className="field">
              <label htmlFor="edit-email">Email</label>
              <input
                id="edit-email"
                type="email"
                value={editing.email}
                onChange={(e) => setEditing({ ...editing, email: e.target.value })}
                required
              />
            </div>

            <div className="field">
              <label htmlFor="edit-department">Department</label>
              <input
                id="edit-department"
                value={editing.department ?? ''}
                onChange={(e) => setEditing({ ...editing, department: e.target.value })}
              />
            </div>

            <div className="field">
              <label htmlFor="edit-role">Role</label>
              <select
                id="edit-role"
                value={editing.userRole}
                onChange={(e) => setEditing({ ...editing, userRole: e.target.value })}
              >
                {USER_ROLES.map((role) => (
                  <option key={role.value} value={role.value}>
                    {role.label}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div className="field">
            <label className="checkbox">
              <input
                type="checkbox"
                checked={editing.isActive}
                onChange={(e) => setEditing({ ...editing, isActive: e.target.checked })}
              />
              Account is active (disabling it signs the user out immediately)
            </label>
          </div>

          <div className="row-actions">
            <button type="submit" className="btn" disabled={busy}>
              Save
            </button>
            <button type="button" className="btn btn-secondary" onClick={() => setEditing(null)}>
              Cancel
            </button>
          </div>
        </form>
      )}

      {resetting && (
        <form className="card narrow" onSubmit={handleReset}>
          <div className="card-title">Reset password for: {resetting.username}</div>

          <div className="field">
            <label htmlFor="reset-password">New password</label>
            <input
              id="reset-password"
              type="text"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              placeholder="At least 8 characters, with letters and digits"
              required
            />
          </div>

          <div className="alert alert-warn">
            The user will have to change this password at their next sign-in, and all of their
            existing sessions will be signed out.
          </div>

          <div className="row-actions">
            <button type="submit" className="btn" disabled={busy || !newPassword}>
              Reset password
            </button>
            <button type="button" className="btn btn-secondary" onClick={() => setResetting(null)}>
              Cancel
            </button>
          </div>
        </form>
      )}

      <form className="card narrow" onSubmit={handleCreate}>
        <div className="card-title">Add a user</div>

        <div className="form-grid">
          <div className="field">
            <label htmlFor="new-username">Username</label>
            <input
              id="new-username"
              value={createForm.username}
              onChange={(e) => setCreateForm({ ...createForm, username: e.target.value })}
              pattern="[A-Za-z0-9._\-]+"
              title="Only A-Z a-z 0-9 . _ - are allowed"
              required
            />
          </div>

          <div className="field">
            <label htmlFor="new-password">Initial password</label>
            <input
              id="new-password"
              type="text"
              value={createForm.password}
              onChange={(e) => setCreateForm({ ...createForm, password: e.target.value })}
              placeholder="At least 8 characters, letters + digits"
              required
            />
          </div>

          <div className="field">
            <label htmlFor="new-fullName">Full name</label>
            <input
              id="new-fullName"
              value={createForm.fullName}
              onChange={(e) => setCreateForm({ ...createForm, fullName: e.target.value })}
              required
            />
          </div>

          <div className="field">
            <label htmlFor="new-email">Email</label>
            <input
              id="new-email"
              type="email"
              value={createForm.email}
              onChange={(e) => setCreateForm({ ...createForm, email: e.target.value })}
              required
            />
          </div>

          <div className="field">
            <label htmlFor="new-department">Department</label>
            <input
              id="new-department"
              value={createForm.department}
              onChange={(e) => setCreateForm({ ...createForm, department: e.target.value })}
            />
          </div>

          <div className="field">
            <label htmlFor="new-role">Role</label>
            <select
              id="new-role"
              value={createForm.userRole}
              onChange={(e) => setCreateForm({ ...createForm, userRole: e.target.value })}
            >
              {USER_ROLES.map((role) => (
                <option key={role.value} value={role.value}>
                  {role.label}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="field">
          <label className="checkbox">
            <input
              type="checkbox"
              checked={createForm.mustChangePassword}
              onChange={(e) => setCreateForm({ ...createForm, mustChangePassword: e.target.checked })}
            />
            Require a password change at first sign-in
          </label>
        </div>

        <button type="submit" className="btn" disabled={busy}>
          Create user
        </button>
      </form>
    </div>
  );
}
