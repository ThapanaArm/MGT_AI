import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDateTime } from '../lib/constants';

const EMPTY_FORM = { name: '', body: '', isShared: false };

export default function SkillsPage() {
  const [skills, setSkills] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editingId, setEditingId] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      setSkills(await api('/api/skills'));
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

  function startEdit(skill) {
    setEditingId(skill.skillId);
    setForm({ name: skill.name, body: skill.body, isShared: skill.isShared });
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
        await api(`/api/skills/${editingId}`, { method: 'PUT', body: form });
        setMessage(`Updated "${form.name}"`);
      } else {
        await api('/api/skills', { method: 'POST', body: form });
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

  async function handleDelete(skill) {
    setError(null);
    setMessage(null);
    try {
      await api(`/api/skills/${skill.skillId}`, { method: 'DELETE' });
      setMessage(`Deleted "${skill.name}"`);
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>Skills</h1>
        <p>
          A personal library of reusable prompt snippets. Click ⚡ next to the chat box to insert
          one straight into your message — a skill has no effect on its own, it just saves typing.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">{editingId ? `Edit "${form.name}"` : 'Create a skill'}</div>

        <form onSubmit={handleSubmit} className="form-grid">
          <div className="field field-wide">
            <label htmlFor="skill-name">Name</label>
            <input
              id="skill-name"
              value={form.name}
              onChange={(e) => patch({ name: e.target.value })}
              placeholder="สรุปอีเมลลูกค้า"
              required
              maxLength={150}
            />
          </div>

          <div className="field field-wide">
            <label htmlFor="skill-body">Snippet text</label>
            <textarea
              id="skill-body"
              rows={5}
              value={form.body}
              onChange={(e) => patch({ body: e.target.value })}
              placeholder="ช่วยสรุปอีเมลด้านล่างนี้เป็นภาษาไทย ให้ประเด็นสำคัญไม่เกิน 5 ข้อ:"
              required
              maxLength={20000}
            />
          </div>

          <div className="field">
            <label>
              <input type="checkbox" checked={form.isShared} onChange={(e) => patch({ isShared: e.target.checked })} />{' '}
              Share with everyone
            </label>
            <div className="faint">Others can use it in their own chats, but only you (or an admin) can edit it.</div>
          </div>

          <div className="field field-wide row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {editingId ? 'Save changes' : 'Create skill'}
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
        <div className="card-title">Your skills ({skills.length})</div>
        {skills.length === 0 && <div className="empty">No skills yet</div>}

        {skills.map((s) => (
          <div key={s.skillId} className="project-row">
            <div className="project-row-head">
              <div>
                <strong>{s.name}</strong>{' '}
                {s.isShared && <span className="badge badge-info">Shared</span>}
                {!s.canEdit && <span className="badge">Not yours</span>}
                <div className="faint">
                  by {s.ownerFullName} · created {formatDateTime(s.createdAt)}
                </div>
              </div>
              {s.canEdit && (
                <div className="row-actions">
                  <button type="button" className="btn-link" onClick={() => startEdit(s)}>Edit</button>
                  <button type="button" className="btn-link btn-link-danger" onClick={() => handleDelete(s)}>
                    Delete
                  </button>
                </div>
              )}
            </div>
            <div className="project-instructions">{s.body}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
