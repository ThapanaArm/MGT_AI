import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDateTime } from '../lib/constants';

const EMPTY_FORM = {
  sourceName: '',
  sourceType: 'Api',
  description: '',
  config: {},
  secret: '',
  isActive: true,
};

/** Which config fields to show per source type, and which of those are secrets. */
const CONFIG_FIELDS = {
  Api: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://api.example.com' },
    {
      key: 'method', label: 'HTTP method', type: 'select',
      options: ['GET', 'POST'], default: 'GET',
    },
    {
      key: 'authType', label: 'Auth type', type: 'select',
      options: ['None', 'ApiKey', 'Bearer', 'Basic'],
    },
    { key: 'apiKeyHeader', label: 'API key header (if Auth type = ApiKey)', placeholder: 'X-API-Key' },
    { key: 'username', label: 'Username (if Auth type = Basic)' },
    {
      key: 'requestBody', label: 'Request body (if HTTP method = POST)', type: 'textarea',
      placeholder: '{ "example": "raw JSON sent as-is, ignored for GET" }',
    },
  ],
  DataLake: [
    { key: 'platform', label: 'Platform (not yet connected — planning note only)', placeholder: 'Databricks / Microsoft Fabric / …' },
  ],
  LocalFolder: [
    { key: 'path', label: 'Folder path on the server', placeholder: 'D:\\Shared\\Sales or \\\\server\\share\\Sales' },
  ],
  SharePoint: [
    { key: 'siteUrl', label: 'Site URL', placeholder: 'https://contoso.sharepoint.com/sites/Sales' },
    { key: 'tenantId', label: 'Entra ID tenant ID' },
    { key: 'clientId', label: 'Entra ID app (client) ID' },
  ],
};

const SECRET_LABEL = {
  Api: 'API key / token / password (per Auth type above)',
  DataLake: 'Secret (platform-specific — not used yet)',
  LocalFolder: null,
  SharePoint: 'Client secret',
};

const TYPE_LABELS = {
  Api: 'API',
  DataLake: 'Data Lake / Lakehouse',
  LocalFolder: 'Local / network folder',
  SharePoint: 'SharePoint',
};

export default function DataSourcesPage() {
  const [sources, setSources] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editingId, setEditingId] = useState(null);
  const [testResults, setTestResults] = useState({});
  const [testing, setTesting] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      setSources(await api('/api/admin/data-sources'));
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

  function patchConfig(key, value) {
    setForm((prev) => ({ ...prev, config: { ...prev.config, [key]: value } }));
  }

  function startEdit(source) {
    setEditingId(source.sourceId);
    setForm({
      sourceName: source.sourceName,
      sourceType: source.sourceType,
      description: source.description ?? '',
      config: source.config ?? {},
      secret: '', // ไม่ส่งความลับเดิมกลับมาให้เห็น — เว้นว่าง = คงค่าเดิมไว้ตอนบันทึก
      isActive: source.isActive,
    });
    setMessage(null);
    setError(null);
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(EMPTY_FORM);
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    const body = {
      sourceName: form.sourceName,
      sourceType: form.sourceType,
      description: form.description || null,
      config: form.config,
      // เว้นว่าง = ไม่แก้ความลับเดิม (backend ตีความ null ต่างจาก "" — "" คือสั่งล้างทิ้ง)
      secret: form.secret === '' ? (editingId ? undefined : null) : form.secret,
      isActive: form.isActive,
    };

    try {
      if (editingId) {
        await api(`/api/admin/data-sources/${editingId}`, { method: 'PUT', body });
        setMessage(`Updated "${form.sourceName}"`);
      } else {
        await api('/api/admin/data-sources', { method: 'POST', body });
        setMessage(`Registered "${form.sourceName}"`);
      }
      cancelEdit();
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete(source) {
    setError(null);
    setMessage(null);
    try {
      await api(`/api/admin/data-sources/${source.sourceId}`, { method: 'DELETE' });
      setMessage(`Deleted "${source.sourceName}"`);
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  async function handleTest(source) {
    setTesting(source.sourceId);
    setError(null);
    try {
      const result = await api(`/api/admin/data-sources/${source.sourceId}/test`, { method: 'POST' });
      setTestResults((prev) => ({ ...prev, [source.sourceId]: result }));
    } catch (err) {
      setTestResults((prev) => ({
        ...prev,
        [source.sourceId]: { success: false, message: err.message, testedAt: new Date().toISOString() },
      }));
    } finally {
      setTesting(null);
    }
  }

  const fields = CONFIG_FIELDS[form.sourceType] ?? [];
  const secretLabel = SECRET_LABEL[form.sourceType];

  return (
    <div className="page">
      <div className="page-header">
        <h1>Data source registry</h1>
        <p >
          Register the systems employees may be given access to — an API, a Data Lake/Lakehouse, a
          local or network folder, or a SharePoint site. This page only builds the registry and
          decides who can read what; nothing in the chat pipeline reads from these sources yet.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">{editingId ? `Edit "${form.sourceName}"` : 'Register a new data source'}</div>

        <form onSubmit={handleSubmit} className="form-grid">
          <div className="field">
            <label htmlFor="sourceName">Name</label>
            <input
              id="sourceName"
              value={form.sourceName}
              onChange={(e) => patch({ sourceName: e.target.value })}
              placeholder="SAP Finance API"
              required
              maxLength={150}
            />
          </div>

          <div className="field">
            <label htmlFor="sourceType">Type</label>
            <select
              id="sourceType"
              value={form.sourceType}
              onChange={(e) => patch({ sourceType: e.target.value, config: {} })}
              disabled={!!editingId}
            >
              {Object.entries(TYPE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
            {editingId && (
              <div className="faint">Type cannot be changed after a source is created — register a new one instead.</div>
            )}
          </div>

          <div className="field field-wide">
            <label htmlFor="description">Description</label>
            <input
              id="description"
              value={form.description}
              onChange={(e) => patch({ description: e.target.value })}
              placeholder="What is this, and who asked for it?"
              maxLength={500}
            />
          </div>

          {form.sourceType === 'DataLake' && (
            <div className="alert alert-warn field-wide">
              No Data Lake / Lakehouse connector is implemented yet. This entry is a placeholder
              for planning — "Test connection" will always report that plainly rather than
              pretending to succeed.
            </div>
          )}

          {fields.map((f) => (
            <div className="field" key={f.key}>
              <label htmlFor={`cfg-${f.key}`}>{f.label}</label>
              {f.type === 'select' ? (
                <select
                  id={`cfg-${f.key}`}
                  value={form.config[f.key] ?? f.default ?? ''}
                  onChange={(e) => patchConfig(f.key, e.target.value)}
                >
                  <option value="">—</option>
                  {f.options.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
              ) : f.type === 'textarea' ? (
                <textarea
                  id={`cfg-${f.key}`}
                  rows={4}
                  value={form.config[f.key] ?? ''}
                  onChange={(e) => patchConfig(f.key, e.target.value)}
                  placeholder={f.placeholder}
                />
              ) : (
                <input
                  id={`cfg-${f.key}`}
                  value={form.config[f.key] ?? ''}
                  onChange={(e) => patchConfig(f.key, e.target.value)}
                  placeholder={f.placeholder}
                />
              )}
            </div>
          ))}

          {secretLabel && (
            <div className="field">
              <label htmlFor="secret">{secretLabel}</label>
              <input
                id="secret"
                type="password"
                value={form.secret}
                onChange={(e) => patch({ secret: e.target.value })}
                placeholder={editingId ? 'Leave blank to keep the current secret' : ''}
                autoComplete="new-password"
              />
              <div className="faint">Stored encrypted. Never shown again after saving — leave blank on edit to keep it unchanged.</div>
            </div>
          )}

          <div className="field">
            <label>
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => patch({ isActive: e.target.checked })}
              />{' '}
              Active
            </label>
          </div>

          <div className="field field-wide row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {editingId ? 'Save changes' : 'Register source'}
            </button>
            {editingId && (
              <button type="button" className="btn btn-secondary" onClick={cancelEdit}>
                Cancel
              </button>
            )}
          </div>
        </form>
      </div>

      <div className="card">
        <div className="card-title">Registered sources ({sources.length})</div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th className="nowrap">Type</th>
                <th>Description</th>
                <th className="nowrap">Secret</th>
                <th className="nowrap">Active grants</th>
                <th className="nowrap">Status</th>
                <th className="nowrap">Last test</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {sources.length === 0 && (
                <tr><td colSpan={8} className="empty">No data sources registered yet</td></tr>
              )}
              {sources.map((s) => {
                const result = testResults[s.sourceId];
                return (
                  <tr key={s.sourceId}>
                    <td><strong>{s.sourceName}</strong></td>
                    <td className="nowrap">{TYPE_LABELS[s.sourceType] ?? s.sourceType}</td>
                    <td>{s.description ?? <span className="faint">—</span>}</td>
                    <td className="nowrap">
                      <span className={s.hasSecret ? 'badge badge-ok' : 'badge'}>
                        {s.hasSecret ? 'Set' : 'None'}
                      </span>
                    </td>
                    <td className="num nowrap">{s.activeGrantCount}</td>
                    <td className="nowrap">
                      <span className={s.isActive ? 'badge badge-ok' : 'badge'}>
                        {s.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td className="nowrap">
                      {result ? (
                        <span
                          className={result.success ? 'badge badge-ok' : 'badge badge-danger'}
                          title={result.message}
                        >
                          {result.success ? 'OK' : 'Failed'} · {formatDateTime(result.testedAt)}
                        </span>
                      ) : (
                        <span className="faint">Not tested</span>
                      )}
                    </td>
                    <td className="nowrap row-actions">
                      <button
                        type="button"
                        className="btn-link"
                        disabled={testing === s.sourceId}
                        onClick={() => handleTest(s)}
                      >
                        {testing === s.sourceId ? 'Testing…' : 'Test'}
                      </button>
                      <button type="button" className="btn-link" onClick={() => startEdit(s)}>
                        Edit
                      </button>
                      <button
                        type="button"
                        className="btn-link btn-link-danger"
                        onClick={() => handleDelete(s)}
                        disabled={s.activeGrantCount > 0}
                        title={s.activeGrantCount > 0 ? 'Revoke all grants before deleting' : ''}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {sources.some((s) => testResults[s.sourceId] && !testResults[s.sourceId].success) && (
          <div className="faint" style={{ marginTop: 8 }}>
            Hover a "Failed" badge to see why the last test failed.
          </div>
        )}
      </div>
    </div>
  );
}
