import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import { formatDateTime } from '../lib/constants';

const SCOPE_LABELS = {
  Full: 'Full — everything this source has',
  OwnDepartment: "Own department only — automatically their Users.Department value",
  Custom: 'Custom — a filter the admin writes below',
};

const EMPTY_FORM = { sourceId: '', userId: '', scopeType: 'Full', scopeFilter: '', notes: '' };

export default function DataSourceGrantsPage() {
  const [sources, setSources] = useState([]);
  const [users, setUsers] = useState([]);
  const [grants, setGrants] = useState([]);
  const [showRevoked, setShowRevoked] = useState(false);
  const [form, setForm] = useState(EMPTY_FORM);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      const [s, u, g] = await Promise.all([
        api('/api/admin/data-sources'),
        api('/api/admin/users'),
        api('/api/admin/data-sources/grants'),
      ]);
      setSources(s);
      setUsers(u);
      setGrants(g);
    } catch (err) {
      setError(err.message);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  function patch(changes) {
    setForm((prev) => ({ ...prev, ...changes }));
  }

  const selectedUser = useMemo(
    () => users.find((u) => String(u.userId) === String(form.userId)),
    [users, form.userId],
  );

  async function handleGrant(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      await api('/api/admin/data-sources/grants', {
        method: 'POST',
        body: {
          sourceId: Number(form.sourceId),
          userId: Number(form.userId),
          scopeType: form.scopeType,
          scopeFilter: form.scopeType === 'Custom' ? form.scopeFilter : null,
          notes: form.notes || null,
        },
      });
      setMessage(`Granted ${selectedUser?.fullName ?? 'the user'} access`);
      setForm(EMPTY_FORM);
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleRevoke(grant) {
    setError(null);
    setMessage(null);
    try {
      await api(`/api/admin/data-sources/grants/${grant.grantId}`, { method: 'DELETE' });
      setMessage(`Revoked ${grant.username}'s access to "${grant.sourceName}"`);
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  const visibleGrants = showRevoked ? grants : grants.filter((g) => g.isActive);

  return (
    <div className="page">
      <div className="page-header">
        <h1>Data source access</h1>
        <p >
          Decide which employee reads how much of each registered source. For example: a CFO gets
          Custom access to the SAP API scoped to the Finance endpoints only, while a Sales Manager
          gets "own department" access so they only ever see their own Division's rows.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">Grant access</div>

        <form onSubmit={handleGrant} className="form-grid">
          <div className="field">
            <label htmlFor="sourceId">Data source</label>
            <select
              id="sourceId"
              value={form.sourceId}
              onChange={(e) => patch({ sourceId: e.target.value })}
              required
            >
              <option value="">— Select —</option>
              {sources.map((s) => (
                <option key={s.sourceId} value={s.sourceId} disabled={!s.isActive}>
                  {s.sourceName} ({s.sourceType}){!s.isActive ? ' — inactive' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="field">
            <label htmlFor="userId">Employee</label>
            <select
              id="userId"
              value={form.userId}
              onChange={(e) => patch({ userId: e.target.value })}
              required
            >
              <option value="">— Select —</option>
              {users.map((u) => (
                <option key={u.userId} value={u.userId} disabled={!u.isActive}>
                  {u.fullName} ({u.username}){u.department ? ` · ${u.department}` : ''}
                  {!u.isActive ? ' — disabled' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="field field-wide">
            <label htmlFor="scopeType">Scope</label>
            <select
              id="scopeType"
              value={form.scopeType}
              onChange={(e) => patch({ scopeType: e.target.value })}
            >
              {Object.entries(SCOPE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
            {form.scopeType === 'OwnDepartment' && (
              <div className="faint">
                {selectedUser
                  ? selectedUser.department
                    ? `Will restrict to Department = "${selectedUser.department}".`
                    : `${selectedUser.fullName} has no Department set — this grant will be refused until one is set.`
                  : 'Restricts to whatever Department is set on the selected employee.'}
              </div>
            )}
          </div>

          {form.scopeType === 'Custom' && (
            <div className="field field-wide">
              <label htmlFor="scopeFilter">Scope filter</label>
              <input
                id="scopeFilter"
                value={form.scopeFilter}
                onChange={(e) => patch({ scopeFilter: e.target.value })}
                placeholder='e.g. endpoints: /finance/*, /gl/* — or a field/row restriction in plain words'
                maxLength={1000}
                required
              />
              <div className="faint">
                Free text describing the restriction — not yet enforced automatically (see the
                registry note on this page's parent). Write it precisely enough that another admin
                reading it later understands exactly what was allowed.
              </div>
            </div>
          )}

          <div className="field field-wide">
            <label htmlFor="notes">Notes</label>
            <input
              id="notes"
              value={form.notes}
              onChange={(e) => patch({ notes: e.target.value })}
              placeholder="Why was this granted? (shown to auditors)"
              maxLength={500}
            />
          </div>

          <div className="field field-wide">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              Grant access
            </button>
          </div>
        </form>
      </div>

      <div className="card">
        <div className="card-title row-actions" style={{ justifyContent: 'space-between' }}>
          <span>Grants ({visibleGrants.length})</span>
          <label style={{ fontWeight: 400 }}>
            <input
              type="checkbox"
              checked={showRevoked}
              onChange={(e) => setShowRevoked(e.target.checked)}
            />{' '}
            Show revoked
          </label>
        </div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Employee</th>
                <th>Data source</th>
                <th className="nowrap">Scope</th>
                <th>Filter / notes</th>
                <th className="nowrap">Granted</th>
                <th className="nowrap">Status</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {visibleGrants.length === 0 && (
                <tr><td colSpan={7} className="empty">No grants yet</td></tr>
              )}
              {visibleGrants.map((g) => (
                <tr key={g.grantId}>
                  <td>
                    {g.fullName}
                    <div className="faint">{g.username}{g.department ? ` · ${g.department}` : ''}</div>
                  </td>
                  <td>{g.sourceName}<div className="faint">{g.sourceType}</div></td>
                  <td className="nowrap">{g.scopeType}</td>
                  <td>
                    {g.scopeFilter && <div>{g.scopeFilter}</div>}
                    {g.notes && <div className="faint">{g.notes}</div>}
                    {!g.scopeFilter && !g.notes && <span className="faint">—</span>}
                  </td>
                  <td className="nowrap">
                    {formatDateTime(g.grantedAt)}
                    <div className="faint">{g.grantedBy}</div>
                  </td>
                  <td className="nowrap">
                    {g.isActive ? (
                      <span className="badge badge-ok">Active</span>
                    ) : (
                      <span className="badge" title={`Revoked ${formatDateTime(g.revokedAt)} by ${g.revokedBy ?? '—'}`}>
                        Revoked
                      </span>
                    )}
                  </td>
                  <td className="nowrap">
                    {g.isActive && (
                      <button type="button" className="btn-link btn-link-danger" onClick={() => handleRevoke(g)}>
                        Revoke
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
