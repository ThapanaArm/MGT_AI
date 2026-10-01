import { useCallback, useEffect, useState } from 'react';
import { api, apiForm } from '../api/client';
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
    { key: 'timeoutMinutes', label: 'Timeout (minutes)', placeholder: '2 (default)' },
    {
      key: 'requestBody', label: 'Request body (if HTTP method = POST)', type: 'textarea',
      placeholder: '{ "example": "raw JSON sent as-is, ignored for GET" }',
    },
  ],
  DataLake: [
    { key: 'tenantId', label: 'Entra ID tenant ID' },
    { key: 'clientId', label: 'Entra ID app (client) ID' },
    {
      key: 'connectionMode', label: 'Connection mode', type: 'select',
      options: ['PowerBi', 'Warehouse', 'GraphQL'], default: 'PowerBi',
    },
    { key: 'datasetId', label: 'Dataset (semantic model) ID (if Connection mode = PowerBi)', placeholder: 'GUID — from the model’s Settings page in Fabric/Power BI' },
    {
      key: 'daxQuery', label: 'DAX query (if Connection mode = PowerBi)', type: 'textarea',
      placeholder: "EVALUATE 'Sales'",
    },
    { key: 'sqlEndpoint', label: 'SQL endpoint (if Connection mode = Warehouse)', placeholder: 'xxxx.datawarehouse.fabric.microsoft.com' },
    { key: 'database', label: 'Database / warehouse name (if Connection mode = Warehouse)', placeholder: 'Sales_DW' },
    {
      key: 'sqlQuery', label: 'SQL query (if Connection mode = Warehouse) — enter more than one SELECT, separated by semicolons, to fetch several tables at once', type: 'textarea',
      placeholder: 'SELECT TOP 1000 * FROM dbo.Sales;\nSELECT TOP 1000 * FROM dbo.Purchases;',
    },
    { key: 'graphqlEndpoint', label: 'GraphQL endpoint URL (if Connection mode = GraphQL)', placeholder: 'https://xxxx.graphql.fabric.microsoft.com/v1/workspaces/.../graphqlapis/.../graphql' },
    {
      key: 'graphqlQuery', label: 'GraphQL query (if Connection mode = GraphQL)', type: 'textarea',
      placeholder: 'query { purchaseOrders(first: 100) { items { id status } } }',
    },
    {
      key: 'graphqlVariables', label: 'GraphQL variables — JSON (if Connection mode = GraphQL, optional)', type: 'textarea',
      placeholder: '{ "status": "Open" }',
    },
    { key: 'timeoutMinutes', label: 'Timeout (minutes)', placeholder: '2 (default)' },
  ],
  LocalFolder: [
    { key: 'path', label: 'Folder path on the server', placeholder: 'D:\\Shared\\Sales or \\\\server\\share\\Sales' },
  ],
  SharePoint: [
    { key: 'siteUrl', label: 'Site URL', placeholder: 'https://contoso.sharepoint.com/sites/Sales' },
    { key: 'tenantId', label: 'Entra ID tenant ID' },
    { key: 'clientId', label: 'Entra ID app (client) ID' },
  ],
  Rag: [],
};

const SECRET_LABEL = {
  Api: 'API key / token / password (per Auth type above)',
  DataLake: 'Client secret',
  LocalFolder: null,
  SharePoint: 'Client secret',
  Rag: null,
};

const TYPE_LABELS = {
  Api: 'API',
  DataLake: 'Data Lake / Lakehouse (Microsoft Fabric)',
  LocalFolder: 'Local / network folder',
  SharePoint: 'SharePoint',
  Rag: 'Knowledge base (RAG)',
};

export default function DataSourcesPage() {
  const [sources, setSources] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editingId, setEditingId] = useState(null);
  const [testResults, setTestResults] = useState({});
  const [testing, setTesting] = useState(null);
  const [importResults, setImportResults] = useState({});
  const [importing, setImporting] = useState(null);
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

  async function handleImportCorpus(source, file) {
    if (!file) return;
    setImporting(source.sourceId);
    setError(null);
    try {
      const formData = new FormData();
      formData.append('file', file);
      const result = await apiForm(`/api/admin/data-sources/${source.sourceId}/rag/import`, formData);
      setImportResults((prev) => ({ ...prev, [source.sourceId]: { success: true, ...result } }));
      setMessage(result.message);
    } catch (err) {
      setImportResults((prev) => ({ ...prev, [source.sourceId]: { success: false, message: err.message } }));
      setError(err.message);
    } finally {
      setImporting(null);
    }
  }

  const fields = CONFIG_FIELDS[form.sourceType] ?? [];
  const secretLabel = SECRET_LABEL[form.sourceType];

  return (
    <div className="page">
      <div className="page-header">
        <h1>Data source registry</h1>
        <p >
          Register the systems employees may be given access to — an API, a Microsoft Fabric /
          Power BI semantic model, a local or network folder, or a SharePoint site. This page
          builds the registry and decides who can read what; grant access on the "Data source
          access" page, then employees can pull it into a chat once they have a grant.
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
              Connects to Microsoft Fabric as a Power BI semantic model queried with DAX ("PowerBi"
              mode), a Fabric Warehouse/Lakehouse SQL analytics endpoint queried with T-SQL
              ("Warehouse" mode), or a Fabric "API for GraphQL" item queried with GraphQL
              ("GraphQL" mode) — pick the mode above; only that mode's fields below are used.
              Warehouse mode's SQL query can be more than one SELECT statement separated by
              semicolons — each one comes back as its own table. Either way, the Entra ID app
              registration needs the right permission (the Power BI
              Service API, e.g. Dataset.Read.All, for PowerBi mode; SQL access granted to the
              service principal for Warehouse mode; Execute permission — "Run Queries and
              Mutations" — on the GraphQL item for GraphQL mode) and a Fabric admin must enable
              "Allow service principals to use Fabric APIs" in the Fabric admin portal — ask your
              IT/Fabric administrator. Until then, "Test connection" will report the exact error
              Fabric, the SQL endpoint, or the GraphQL endpoint returns.
            </div>
          )}

          {form.sourceType === 'Rag' && (
            <div className="alert alert-warn field-wide">
              Holds a pre-chunked knowledge base (e.g. policy or reference documents already split
              into passages) searched by meaning rather than queried live. Needs no connection
              fields or secret here — register the source first, then use "Import corpus" in the
              table below to upload a JSONL file (one JSON object per line, each with at least a
              "text" field; "source_file", "page", "chapter" and "section" are used for citations
              if present). Re-importing replaces everything previously imported for this source.
              Requires an OpenAI API key configured on the server (used only to generate
              embeddings, independent of which AI provider answers the chat).
            </div>
          )}

          {fields.map((f) => (
            <div className={f.type === 'textarea' ? 'field field-wide' : 'field'} key={f.key}>
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
                  className="field-code"
                  rows={10}
                  value={form.config[f.key] ?? ''}
                  onChange={(e) => patchConfig(f.key, e.target.value)}
                  placeholder={f.placeholder}
                  spellCheck={false}
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
                      {s.sourceType === 'Rag' && (
                        <label
                          className="btn-link"
                          style={{ cursor: importing === s.sourceId ? 'default' : 'pointer' }}
                          title={importResults[s.sourceId]?.message}
                        >
                          {importing === s.sourceId ? 'Importing…' : 'Import corpus'}
                          <input
                            type="file"
                            accept=".jsonl,.txt"
                            style={{ display: 'none' }}
                            disabled={importing === s.sourceId}
                            onChange={(e) => {
                              const file = e.target.files?.[0];
                              e.target.value = '';
                              handleImportCorpus(s, file);
                            }}
                          />
                        </label>
                      )}
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
