import { useCallback, useEffect, useRef, useState } from 'react';
import { api, apiForm, download } from '../api/client';
import { formatDateTime, formatFileSize, formatNumber, formatThb, formatUsd } from '../lib/constants';

const ACCEPT = '.pdf,.png,.jpg,.jpeg,.webp,.gif';

export default function RagCreatePage() {
  const [categories, setCategories] = useState([]);
  const [sources, setSources] = useState([]);
  const [models, setModels] = useState([]);
  const [runs, setRuns] = useState([]);

  const [mode, setMode] = useState('new');
  const [sourceName, setSourceName] = useState('');
  const [description, setDescription] = useState('');
  const [existingId, setExistingId] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [subCategoryId, setSubCategoryId] = useState('');
  const [model, setModel] = useState('');
  const [pagesPerBatch, setPagesPerBatch] = useState(3);
  const [files, setFiles] = useState([]);

  const [job, setJob] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);
  const [editing, setEditing] = useState(null);
  const fileInput = useRef(null);

  const loadSources = useCallback(async () => {
    try {
      setSources(await api('/api/admin/rag/sources'));
    } catch (err) {
      setError(err.message);
    }
  }, []);

  const loadRuns = useCallback(async () => {
    try {
      setRuns(await api('/api/admin/rag/runs'));
    } catch (err) {
      setError(err.message);
    }
  }, []);

  useEffect(() => {
    loadRuns();
    loadSources();
    api('/api/admin/rag/categories').then(setCategories).catch((err) => setError(err.message));
    api('/api/chat/models')
      .then((list) => {
        const ready = list.filter((m) => m.providerReady);
        setModels(ready);
        // Cheap "lite" models drop Thai tone marks when reading pages — default to a full Flash-class model.
        const preferred = ready.find((m) => m.provider === 'Google' && m.name.includes('flash') && !m.name.includes('lite'));
        setModel((current) => current || preferred?.name || ready[0]?.name || '');
      })
      .catch((err) => setError(err.message));
  }, [loadSources, loadRuns]);

  // Poll the background job until it finishes.
  const jobId = job?.jobId;
  const jobState = job?.state;
  useEffect(() => {
    if (!jobId || jobState === 'Completed' || jobState === 'Failed') return undefined;

    const timer = setInterval(async () => {
      try {
        const next = await api(`/api/admin/rag/jobs/${jobId}`);
        setJob(next);
        if (next.state === 'Completed' || next.state === 'Failed') {
          loadSources();
          loadRuns();
        }
      } catch (err) {
        setError(err.message);
      }
    }, 2000);

    return () => clearInterval(timer);
  }, [jobId, jobState, loadSources, loadRuns]);

  const selectedCategory = categories.find((c) => String(c.categoryId) === categoryId);
  const running = job && (job.state === 'Queued' || job.state === 'Running');
  const selectedModel = models.find((m) => m.name === model);
  const hasPdf = files.some((f) => f.name.toLowerCase().endsWith('.pdf'));
  const qwenPdf = hasPdf && selectedModel?.provider === 'Qwen';

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      const form = new FormData();
      if (mode === 'existing') {
        form.append('sourceId', existingId);
      } else {
        form.append('sourceName', sourceName);
        if (description) form.append('description', description);
        if (categoryId) form.append('categoryId', categoryId);
        if (subCategoryId) form.append('subCategoryId', subCategoryId);
      }
      form.append('model', model);
      form.append('pagesPerBatch', String(pagesPerBatch));
      files.forEach((f) => form.append('files', f));

      setJob(await apiForm('/api/admin/rag/create', form));
      loadRuns();
      setFiles([]);
      if (fileInput.current) fileInput.current.value = '';
      await loadSources();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleExport(source, includeVectors, zip = false) {
    setError(null);
    try {
      await download(
        `/api/admin/rag/sources/${source.sourceId}/export${zip ? '.zip' : ''}${includeVectors ? '?includeVectors=true' : ''}`,
        `${source.sourceName}.${zip ? 'zip' : 'jsonl'}`,
      );
    } catch (err) {
      setError(err.message);
    }
  }

  async function saveEdit(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    try {
      await api(`/api/admin/rag/sources/${editing.sourceId}`, {
        method: 'PUT',
        body: {
          name: editing.name,
          description: editing.description || null,
          isActive: editing.isActive,
          categoryId: editing.categoryId ? Number(editing.categoryId) : null,
          subCategoryId: editing.subCategoryId ? Number(editing.subCategoryId) : null,
        },
      });
      setEditing(null);
      setMessage(`Saved "${editing.name}"`);
      await loadSources();
      setCategories(await api('/api/admin/rag/categories'));
    } catch (err) {
      setError(err.message);
    }
  }

  async function handleDelete(source) {
    const ok = window.confirm(
      `Delete "${source.sourceName}"?\n\n`
      + `This permanently deletes all ${source.chunkCount} chunk(s) and removes every employee access to it. `
      + 'Conversations that used it are kept but no longer linked to it. This cannot be undone.',
    );
    if (!ok) return;

    setError(null);
    setMessage(null);
    try {
      await api(`/api/admin/rag/sources/${source.sourceId}`, { method: 'DELETE' });
      if (existingId === String(source.sourceId)) setExistingId('');
      setMessage(`Deleted "${source.sourceName}"`);
      await loadSources();
      setCategories(await api('/api/admin/rag/categories'));
    } catch (err) {
      setError(err.message);
    }
  }

  const progressPct = job && job.pagesTotal > 0 ? Math.round((job.pagesDone / job.pagesTotal) * 100) : 0;
  const editingCategory = categories.find((c) => String(c.categoryId) === editing?.categoryId);
  const canSubmit = files.length > 0 && model && (mode === 'existing' ? existingId : sourceName.trim());

  return (
    <div className="page">
      <div className="page-header">
        <h1>Create RAG</h1>
        <p>
          Upload PDF or image files — an AI vision model reads (OCRs) every page, the text is split
          into passages and embedded. The text is kept verbatim in the original language of the
          document and never translated. The result becomes a searchable RAG you can grant to
          employees on the "Data source access" tab. Download it as JSONL to load into Microsoft
          Fabric.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">Create or extend a RAG</div>
        <form onSubmit={handleSubmit} className="form-grid">
          <div className="field field-wide" style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>
            <label style={{ display: 'inline-flex', alignItems: 'center', gap: 8, margin: 0 }}>
              <input type="radio" style={{ width: 'auto' }} checked={mode === 'new'} onChange={() => setMode('new')} />
              New RAG
            </label>
            <label style={{ display: 'inline-flex', alignItems: 'center', gap: 8, margin: 0 }}>
              <input
                type="radio"
                style={{ width: 'auto' }}
                checked={mode === 'existing'}
                onChange={() => setMode('existing')}
                disabled={sources.length === 0}
              />
              Add to an existing RAG
            </label>
          </div>

          {mode === 'new' ? (
            <>
              <div className="field">
                <label htmlFor="rag-name">RAG name</label>
                <input
                  id="rag-name"
                  value={sourceName}
                  onChange={(e) => setSourceName(e.target.value)}
                  placeholder="MGT Accounting Knowledge"
                  maxLength={150}
                  required
                />
              </div>
              <div className="field">
                <label htmlFor="rag-desc">Description</label>
                <input
                  id="rag-desc"
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  placeholder="What is in it, and who asked for it?"
                  maxLength={500}
                />
              </div>
              <div className="field">
                <label htmlFor="rag-cat">Category</label>
                <select
                  id="rag-cat"
                  value={categoryId}
                  onChange={(e) => { setCategoryId(e.target.value); setSubCategoryId(''); }}
                >
                  <option value="">— none —</option>
                  {categories.filter((c) => c.isActive).map((c) => (
                    <option key={c.categoryId} value={c.categoryId}>{c.categoryName}</option>
                  ))}
                </select>
              </div>
              <div className="field">
                <label htmlFor="rag-sub">Sub category</label>
                <select
                  id="rag-sub"
                  value={subCategoryId}
                  onChange={(e) => setSubCategoryId(e.target.value)}
                  disabled={!selectedCategory}
                >
                  <option value="">— none —</option>
                  {(selectedCategory?.subCategories ?? []).filter((s) => s.isActive).map((s) => (
                    <option key={s.subCategoryId} value={s.subCategoryId}>{s.subCategoryName}</option>
                  ))}
                </select>
                {categories.length === 0 && (
                  <div className="faint">No categories yet — add them on the "Setting RAG Category" tab.</div>
                )}
              </div>
            </>
          ) : (
            <div className="field field-wide">
              <label htmlFor="rag-existing">RAG to add the files to</label>
              <select id="rag-existing" value={existingId} onChange={(e) => setExistingId(e.target.value)} required>
                <option value="">— choose —</option>
                {sources.map((s) => (
                  <option key={s.sourceId} value={s.sourceId}>
                    {s.sourceName} ({s.chunkCount} chunks)
                  </option>
                ))}
              </select>
              <div className="faint">A file with the same name as one already in this RAG replaces its earlier chunks.</div>
            </div>
          )}

          <div className="field">
            <label htmlFor="rag-model">Model that reads the files (OCR)</label>
            <select id="rag-model" value={model} onChange={(e) => setModel(e.target.value)} required>
              {models.map((m) => (
                <option key={m.name} value={m.name}>{m.name} — {m.provider}</option>
              ))}
            </select>
            <div className="faint">
              Billed to that provider per page. Claude, Gemini and OpenAI read PDFs; Qwen reads images
              only. Cheapest "lite" models can lose Thai tone marks — use a Flash-class model or better.
            </div>
          </div>

          <div className="field">
            <label htmlFor="rag-batch">PDF pages per AI call</label>
            <input
              id="rag-batch"
              type="number"
              min={1}
              max={10}
              value={pagesPerBatch}
              onChange={(e) => setPagesPerBatch(Number(e.target.value) || 1)}
            />
            <div className="faint">Smaller = more accurate on dense pages, more calls.</div>
          </div>

          <div className="field field-wide">
            <label htmlFor="rag-files">Files (PDF, PNG, JPG, WEBP, GIF)</label>
            <input
              id="rag-files"
              ref={fileInput}
              type="file"
              accept={ACCEPT}
              multiple
              onChange={(e) => setFiles(Array.from(e.target.files ?? []))}
            />
            {files.length > 0 && (
              <ul className="faint" style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                {files.map((f) => <li key={f.name}>{f.name} — {formatFileSize(f.size)}</li>)}
              </ul>
            )}
            {qwenPdf && (
              <div className="alert alert-warn" style={{ marginTop: 8 }}>
                Qwen cannot read PDF files — pick a Claude, Gemini or OpenAI model for these.
              </div>
            )}
          </div>

          <div className="field field-wide row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy || running || !canSubmit || qwenPdf}>
              {busy ? 'Uploading…' : 'Create RAG'}
            </button>
          </div>
        </form>
      </div>

      {job && (
        <div className="card">
          <div className="card-title">
            Progress — {job.sourceName}{' '}
            <span className={job.state === 'Completed' ? 'badge badge-ok' : job.state === 'Failed' ? 'badge badge-danger' : 'badge'}>
              {job.state}
            </span>
          </div>

          <div className="faint" style={{ marginBottom: 6 }}>{job.phase}</div>
          <div style={{ height: 10, background: 'var(--surface-2)', borderRadius: 6, overflow: 'hidden' }}>
            <div style={{ width: `${progressPct}%`, height: '100%', background: 'var(--brand)', transition: 'width .4s' }} />
          </div>
          <div className="faint" style={{ marginTop: 6 }}>
            Pages {job.pagesDone}/{job.pagesTotal} · files {job.filesDone}/{job.filesTotal} ·{' '}
            {formatNumber(job.chunksCreated)} chunk(s) · model {job.model}
          </div>

          <div className="table-wrap" style={{ marginTop: 10 }}>
            <table>
              <thead>
                <tr>
                  <th>Step</th>
                  <th className="nowrap">Tokens</th>
                  <th className="nowrap">Cost (USD)</th>
                  <th className="nowrap">Cost (THB)</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td>
                    Reading pages (OCR) — {job.model}
                    <div className="faint">
                      {formatNumber(job.inputTokens)} in · {formatNumber(job.outputTokens)} out
                      {job.cacheTokens > 0 ? ` · ${formatNumber(job.cacheTokens)} cached` : ''}
                    </div>
                  </td>
                  <td className="num nowrap">{formatNumber(job.inputTokens + job.outputTokens + job.cacheTokens)}</td>
                  <td className="num nowrap">${formatUsd(job.ocrCostUsd)}</td>
                  <td className="num nowrap">{formatThb(job.ocrCostUsd * job.usdToThbRate)}</td>
                </tr>
                <tr>
                  <td>Embeddings — text-embedding-3-small</td>
                  <td className="num nowrap">{formatNumber(job.embeddingTokens)}</td>
                  <td className="num nowrap">${formatUsd(job.embeddingCostUsd)}</td>
                  <td className="num nowrap">{formatThb(job.embeddingCostUsd * job.usdToThbRate)}</td>
                </tr>
                <tr>
                  <td><strong>Total{job.state === 'Running' ? ' so far' : ''}</strong></td>
                  <td className="num nowrap">
                    <strong>{formatNumber(job.inputTokens + job.outputTokens + job.cacheTokens + job.embeddingTokens)}</strong>
                  </td>
                  <td className="num nowrap"><strong>${formatUsd(job.totalCostUsd)}</strong></td>
                  <td className="num nowrap"><strong>{formatThb(job.totalCostThb)}</strong></td>
                </tr>
              </tbody>
            </table>
          </div>
          <div className="faint" style={{ marginTop: 4 }}>
            Priced from the model pricing page (1 USD = {formatThb(job.usdToThbRate)} THB); embeddings at OpenAI's list price.
          </div>

          {job.error && <div className="alert alert-danger" style={{ marginTop: 10 }}>{job.error}</div>}
          {job.warnings.length > 0 && (
            <div className="alert alert-warn" style={{ marginTop: 10 }}>
              <strong>{job.warnings.length} warning(s)</strong>
              <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
                {job.warnings.map((w) => <li key={w}>{w}</li>)}
              </ul>
            </div>
          )}

          {job.state === 'Completed' && (
            <div className="row-actions" style={{ marginTop: 12 }}>
              <button
                type="button"
                className="btn btn-secondary"
                onClick={() => handleExport({ sourceId: job.sourceId, sourceName: job.sourceName }, false, true)}
              >
                Download ZIP (for Microsoft Fabric)
              </button>
              <button
                type="button"
                className="btn btn-secondary"
                onClick={() => handleExport({ sourceId: job.sourceId, sourceName: job.sourceName }, false)}
              >
                Download JSONL
              </button>
            </div>
          )}
        </div>
      )}

      <div className="card">
        <div className="card-title">RAG knowledge bases ({sources.length})</div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Category</th>
                <th className="nowrap">Status</th>
                <th className="nowrap">Files</th>
                <th className="nowrap">Chunks</th>
                <th className="nowrap">Created</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {sources.length === 0 && (
                <tr><td colSpan={7} className="empty">No RAGs yet</td></tr>
              )}
              {sources.map((s) =>
                editing?.sourceId === s.sourceId ? (
                  <tr key={s.sourceId}>
                    <td colSpan={7}>
                      <form onSubmit={saveEdit} className="row-actions" style={{ flexWrap: 'wrap', gap: 8 }}>
                        <input
                          value={editing.name}
                          onChange={(e) => setEditing((p) => ({ ...p, name: e.target.value }))}
                          placeholder="RAG name"
                          required
                          maxLength={150}
                        />
                        <input
                          value={editing.description}
                          onChange={(e) => setEditing((p) => ({ ...p, description: e.target.value }))}
                          placeholder="Description"
                          maxLength={500}
                          style={{ minWidth: 240 }}
                        />
                        <select
                          value={editing.categoryId}
                          onChange={(e) => setEditing((p) => ({ ...p, categoryId: e.target.value, subCategoryId: '' }))}
                        >
                          <option value="">— no category —</option>
                          {categories.map((c) => <option key={c.categoryId} value={c.categoryId}>{c.categoryName}</option>)}
                        </select>
                        <select
                          value={editing.subCategoryId}
                          onChange={(e) => setEditing((p) => ({ ...p, subCategoryId: e.target.value }))}
                          disabled={!editingCategory}
                        >
                          <option value="">— no sub category —</option>
                          {(editingCategory?.subCategories ?? []).map((sc) => (
                            <option key={sc.subCategoryId} value={sc.subCategoryId}>{sc.subCategoryName}</option>
                          ))}
                        </select>
                        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, margin: 0 }}>
                          <input
                            type="checkbox"
                            style={{ width: 'auto' }}
                            checked={editing.isActive}
                            onChange={(e) => setEditing((p) => ({ ...p, isActive: e.target.checked }))}
                          />
                          Active
                        </label>
                        <button type="submit" className="btn btn-sm">Save</button>
                        <button type="button" className="btn btn-secondary btn-sm" onClick={() => setEditing(null)}>Cancel</button>
                      </form>
                    </td>
                  </tr>
                ) : (
                  <tr key={s.sourceId}>
                    <td>
                      <strong>{s.sourceName}</strong>
                      {s.description && <div className="faint">{s.description}</div>}
                    </td>
                    <td>
                      {s.categoryName
                        ? `${s.categoryName}${s.subCategoryName ? ` › ${s.subCategoryName}` : ''}`
                        : <span className="faint">Uncategorised</span>}
                    </td>
                    <td className="nowrap">
                      <span className={s.isActive ? 'badge badge-ok' : 'badge'}>{s.isActive ? 'Active' : 'Inactive'}</span>
                    </td>
                    <td className="num nowrap">{s.fileCount}</td>
                    <td className="num nowrap">{formatNumber(s.chunkCount)}</td>
                    <td className="nowrap">{formatDateTime(s.createdAt)}</td>
                    <td className="nowrap row-actions">
                      <button
                        type="button"
                        className="btn-link"
                        onClick={() => setEditing({
                          sourceId: s.sourceId,
                          name: s.sourceName,
                          description: s.description ?? '',
                          isActive: s.isActive,
                          categoryId: s.categoryId ? String(s.categoryId) : '',
                          subCategoryId: s.subCategoryId ? String(s.subCategoryId) : '',
                        })}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className="btn-link"
                        disabled={s.chunkCount === 0}
                        onClick={() => handleExport(s, false, true)}
                        title="ZIP with the JSONL, a manifest and a README on loading it into a Fabric Lakehouse"
                      >
                        Download ZIP
                      </button>
                      <button
                        type="button"
                        className="btn-link"
                        disabled={s.chunkCount === 0}
                        onClick={() => handleExport(s, false)}
                        title="JSONL only, one passage per line"
                      >
                        JSONL
                      </button>
                      <button
                        type="button"
                        className="btn-link"
                        disabled={s.chunkCount === 0}
                        onClick={() => handleExport(s, true, true)}
                        title="ZIP that also includes the 1536-dimension embedding of each passage (large file)"
                      >
                        ZIP + vectors
                      </button>
                      <button type="button" className="btn-link btn-link-danger" onClick={() => handleDelete(s)}>
                        Delete
                      </button>
                    </td>
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
      </div>
      <div className="card">
        <div className="card-title">
          Run history — tokens and cost
          {runs.length > 0 && (
            <span className="faint" style={{ marginLeft: 12 }}>
              total {formatNumber(runs.length)} run(s): ${formatUsd(runs.reduce((a, r) => a + r.totalCostUsd, 0))}
              {' '}/ {formatThb(runs.reduce((a, r) => a + r.totalCostThb, 0))} THB
            </span>
          )}
        </div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="nowrap">Started</th>
                <th>RAG</th>
                <th>Model</th>
                <th className="nowrap">Status</th>
                <th className="nowrap">Pages</th>
                <th className="nowrap">Chunks</th>
                <th className="nowrap">OCR tokens</th>
                <th className="nowrap">Embedding tokens</th>
                <th className="nowrap">Cost (USD)</th>
                <th className="nowrap">Cost (THB)</th>
              </tr>
            </thead>
            <tbody>
              {runs.length === 0 && (
                <tr><td colSpan={10} className="empty">No runs yet</td></tr>
              )}
              {runs.map((r) => (
                <tr key={r.runId}>
                  <td className="nowrap">{formatDateTime(r.startedAt)}</td>
                  <td>
                    {r.sourceName}
                    {r.startedBy && <div className="faint">by {r.startedBy}</div>}
                  </td>
                  <td className="nowrap">{r.model}</td>
                  <td className="nowrap">
                    <span
                      className={r.state === 'Completed' ? 'badge badge-ok' : r.state === 'Running' ? 'badge' : 'badge badge-danger'}
                      title={r.error ?? ''}
                    >
                      {r.state}
                    </span>
                  </td>
                  <td className="num nowrap">{r.pagesTotal}</td>
                  <td className="num nowrap">{formatNumber(r.chunksCreated)}</td>
                  <td className="num nowrap" title={`${formatNumber(r.inputTokens)} in / ${formatNumber(r.outputTokens)} out / ${formatNumber(r.cacheTokens)} cached`}>
                    {formatNumber(r.inputTokens + r.outputTokens + r.cacheTokens)}
                  </td>
                  <td className="num nowrap">{formatNumber(r.embeddingTokens)}</td>
                  <td className="num nowrap">${formatUsd(r.totalCostUsd)}</td>
                  <td className="num nowrap">{formatThb(r.totalCostThb)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
