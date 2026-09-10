import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, apiForm, download } from '../api/client';
import { formatDateTime, formatFileSize, fileKindIcon } from '../lib/constants';

const EMPTY_FORM = { name: '', instructions: '', isShared: false };

export default function ProjectsPage() {
  const [projects, setProjects] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editingId, setEditingId] = useState(null);
  const [expandedId, setExpandedId] = useState(null);
  const [files, setFiles] = useState({});
  const [uploading, setUploading] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      setProjects(await api('/api/projects'));
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

  function startEdit(project) {
    setEditingId(project.projectId);
    setForm({ name: project.name, instructions: project.instructions ?? '', isShared: project.isShared });
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

    try {
      if (editingId) {
        await api(`/api/projects/${editingId}`, { method: 'PUT', body: form });
        setMessage(`Updated "${form.name}"`);
      } else {
        await api('/api/projects', { method: 'POST', body: form });
        setMessage(`Created "${form.name}"`);
      }
      cancelEdit();
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete(project) {
    setError(null);
    setMessage(null);
    try {
      await api(`/api/projects/${project.projectId}`, { method: 'DELETE' });
      setMessage(`Deleted "${project.name}" — its past conversations remain readable for auditing`);
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  async function toggleFiles(project) {
    if (expandedId === project.projectId) {
      setExpandedId(null);
      return;
    }
    setExpandedId(project.projectId);
    if (!files[project.projectId]) {
      try {
        const list = await api(`/api/projects/${project.projectId}/files`);
        setFiles((prev) => ({ ...prev, [project.projectId]: list }));
      } catch (err) {
        setError(err.message);
      }
    }
  }

  async function handleUpload(project, fileList) {
    const picked = fileList?.[0];
    if (!picked) return;

    setUploading(project.projectId);
    setError(null);
    setMessage(null);

    try {
      const form = new FormData();
      form.append('file', picked, picked.name);
      await apiForm(`/api/projects/${project.projectId}/files`, form);
      const list = await api(`/api/projects/${project.projectId}/files`);
      setFiles((prev) => ({ ...prev, [project.projectId]: list }));
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setUploading(null);
    }
  }

  async function handleDeleteFile(project, file) {
    setError(null);
    try {
      await api(`/api/projects/files/${file.projectFileId}`, { method: 'DELETE' });
      setFiles((prev) => ({
        ...prev,
        [project.projectId]: prev[project.projectId].filter((f) => f.projectFileId !== file.projectFileId),
      }));
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>Projects</h1>
        <p>
          A workspace with standing instructions and reference files — every conversation started
          under a project gets both automatically, on every message. Share a project and everyone
          can start chats in it too; only you (or an administrator) can edit it or its files.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">{editingId ? `Edit "${form.name}"` : 'Create a project'}</div>

        <form onSubmit={handleSubmit} className="form-grid">
          <div className="field field-wide">
            <label htmlFor="name">Name</label>
            <input
              id="name"
              value={form.name}
              onChange={(e) => patch({ name: e.target.value })}
              placeholder="Sales Playbook"
              required
              maxLength={150}
            />
          </div>

          <div className="field field-wide">
            <label htmlFor="instructions">Instructions</label>
            <textarea
              id="instructions"
              rows={4}
              value={form.instructions}
              onChange={(e) => patch({ instructions: e.target.value })}
              placeholder="Reply about pricing/promotions in Thai, and always quote prices from the attached files."
              maxLength={20000}
            />
            <div className="faint">Appended to every answer's instructions for conversations in this project.</div>
          </div>

          <div className="field">
            <label>
              <input type="checkbox" checked={form.isShared} onChange={(e) => patch({ isShared: e.target.checked })} />{' '}
              Share with everyone
            </label>
            <div className="faint">Others can use it and see its files, but only you (or an admin) can edit it.</div>
          </div>

          <div className="field field-wide row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {editingId ? 'Save changes' : 'Create project'}
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
        <div className="card-title">Your projects ({projects.length})</div>
        {projects.length === 0 && <div className="empty">No projects yet</div>}

        {projects.map((p) => (
          <div key={p.projectId} className="project-row">
            <div className="project-row-head">
              <div>
                <strong>{p.name}</strong>{' '}
                {p.isShared && <span className="badge badge-info">Shared</span>}
                {!p.canEdit && <span className="badge">Not yours</span>}
                <div className="faint">
                  by {p.ownerFullName} · {p.sessionCount} conversation(s) · {p.fileCount} file(s) ·
                  created {formatDateTime(p.createdAt)}
                </div>
              </div>
              <div className="row-actions">
                <Link className="btn btn-secondary btn-sm" to={`/chat?project=${p.projectId}`}>
                  + New chat in project
                </Link>
                <button type="button" className="btn-link" onClick={() => toggleFiles(p)}>
                  {expandedId === p.projectId ? 'Hide files' : 'Files'}
                </button>
                {p.canEdit && (
                  <>
                    <button type="button" className="btn-link" onClick={() => startEdit(p)}>Edit</button>
                    <button type="button" className="btn-link btn-link-danger" onClick={() => handleDelete(p)}>
                      Delete
                    </button>
                  </>
                )}
              </div>
            </div>

            {p.instructions && <div className="project-instructions">{p.instructions}</div>}

            {expandedId === p.projectId && (
              <div className="project-files">
                {p.canEdit && (
                  <label className="btn btn-secondary btn-sm" style={{ display: 'inline-block', cursor: 'pointer' }}>
                    {uploading === p.projectId ? 'Uploading…' : '+ Add file'}
                    <input
                      type="file"
                      hidden
                      disabled={uploading === p.projectId}
                      onChange={(e) => { handleUpload(p, e.target.files); e.target.value = ''; }}
                    />
                  </label>
                )}

                {(files[p.projectId] ?? []).length === 0 ? (
                  <div className="faint" style={{ marginTop: 6 }}>No files yet</div>
                ) : (
                  <table style={{ marginTop: 8 }}>
                    <thead>
                      <tr>
                        <th>File</th>
                        <th className="nowrap">Size</th>
                        <th className="nowrap">Scanned</th>
                        <th className="nowrap">Uploaded by</th>
                        <th className="nowrap">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(files[p.projectId] ?? []).map((f) => (
                        <tr key={f.projectFileId}>
                          <td>{fileKindIcon(f.fileKind)} {f.fileName}</td>
                          <td className="nowrap">{formatFileSize(f.sizeBytes)}</td>
                          <td className="nowrap">
                            <span className={f.policyScanned ? 'badge badge-ok' : 'badge'}>
                              {f.policyScanned ? 'Content' : 'Name only'}
                            </span>
                          </td>
                          <td className="nowrap">{f.uploadedByUsername}</td>
                          <td className="nowrap row-actions">
                            <button
                              type="button"
                              className="btn-link"
                              onClick={() => download(`/api/projects/files/${f.projectFileId}`, f.fileName)}
                            >
                              Download
                            </button>
                            {p.canEdit && (
                              <button
                                type="button"
                                className="btn-link btn-link-danger"
                                onClick={() => handleDeleteFile(p, f)}
                              >
                                Remove
                              </button>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
