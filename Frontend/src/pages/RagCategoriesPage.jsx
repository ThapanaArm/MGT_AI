import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';

const EMPTY = { name: '', description: '' };

export default function RagCategoriesPage() {
  const [categories, setCategories] = useState([]);
  const [newCategory, setNewCategory] = useState(EMPTY);
  const [newSub, setNewSub] = useState({});
  const [editing, setEditing] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      setCategories(await api('/api/admin/rag/categories'));
    } catch (err) {
      setError(err.message);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function run(action, successMessage) {
    setError(null);
    setMessage(null);
    setBusy(true);
    try {
      await action();
      if (successMessage) setMessage(successMessage);
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  function addCategory(event) {
    event.preventDefault();
    run(async () => {
      await api('/api/admin/rag/categories', {
        method: 'POST',
        body: { name: newCategory.name, description: newCategory.description || null, isActive: true },
      });
      setNewCategory(EMPTY);
    }, `Added category "${newCategory.name}"`);
  }

  function addSub(event, category) {
    event.preventDefault();
    const draft = newSub[category.categoryId] ?? EMPTY;
    run(async () => {
      await api('/api/admin/rag/subcategories', {
        method: 'POST',
        body: {
          categoryId: category.categoryId,
          name: draft.name,
          description: draft.description || null,
          isActive: true,
        },
      });
      setNewSub((prev) => ({ ...prev, [category.categoryId]: EMPTY }));
    }, `Added sub category "${draft.name}" under "${category.categoryName}"`);
  }

  function saveEdit(event) {
    event.preventDefault();
    const e = editing;
    run(async () => {
      if (e.kind === 'category') {
        await api(`/api/admin/rag/categories/${e.id}`, {
          method: 'PUT',
          body: { name: e.name, description: e.description || null, isActive: e.isActive },
        });
      } else {
        await api(`/api/admin/rag/subcategories/${e.id}`, {
          method: 'PUT',
          body: { categoryId: e.categoryId, name: e.name, description: e.description || null, isActive: e.isActive },
        });
      }
      setEditing(null);
    }, `Saved "${e.name}"`);
  }

  function remove(kind, id, name, detail) {
    if (!window.confirm(`Delete "${name}"?${detail ? `

${detail}` : ''}`)) return;
    run(async () => {
      await api(`/api/admin/rag/${kind === 'category' ? 'categories' : 'subcategories'}/${id}`, { method: 'DELETE' });
    }, `Deleted "${name}"`);
  }

  function startEditCategory(c) {
    setEditing({ kind: 'category', id: c.categoryId, name: c.categoryName, description: c.description ?? '', isActive: c.isActive });
  }

  function startEditSub(s) {
    setEditing({
      kind: 'sub', id: s.subCategoryId, categoryId: s.categoryId,
      name: s.subCategoryName, description: s.description ?? '', isActive: s.isActive,
    });
  }

  const isEditing = (kind, id) => editing?.kind === kind && editing.id === id;

  const editForm = (
    <form onSubmit={saveEdit} className="row-actions" style={{ flexWrap: 'wrap', gap: 8 }}>
      <input
        value={editing?.name ?? ''}
        onChange={(e) => setEditing((p) => ({ ...p, name: e.target.value }))}
        placeholder="Name"
        required
        maxLength={150}
      />
      <input
        value={editing?.description ?? ''}
        onChange={(e) => setEditing((p) => ({ ...p, description: e.target.value }))}
        placeholder="Description (optional)"
        maxLength={500}
      />
      <label>
        <input
          type="checkbox"
          checked={editing?.isActive ?? true}
          onChange={(e) => setEditing((p) => ({ ...p, isActive: e.target.checked }))}
        />{' '}
        Active
      </label>
      <button type="submit" className="btn btn-sm" disabled={busy}>Save</button>
      <button type="button" className="btn btn-secondary btn-sm" onClick={() => setEditing(null)}>Cancel</button>
    </form>
  );

  return (
    <div className="page">
      <div className="page-header">
        <h1>Setting RAG Category</h1>
        <p>
          Organise RAG knowledge bases into a Category and Sub Category (for example Accounting →
          Tax). Pick them when you create a RAG, or file an existing RAG on the "Create RAG" tab.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="card">
        <div className="card-title">Add a category</div>
        <form onSubmit={addCategory} className="form-grid">
          <div className="field">
            <label htmlFor="cat-name">Category name</label>
            <input
              id="cat-name"
              value={newCategory.name}
              onChange={(e) => setNewCategory((p) => ({ ...p, name: e.target.value }))}
              placeholder="Accounting"
              required
              maxLength={150}
            />
          </div>
          <div className="field">
            <label htmlFor="cat-desc">Description</label>
            <input
              id="cat-desc"
              value={newCategory.description}
              onChange={(e) => setNewCategory((p) => ({ ...p, description: e.target.value }))}
              placeholder="Optional"
              maxLength={500}
            />
          </div>
          <div className="field field-wide row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>Add category</button>
          </div>
        </form>
      </div>

      {categories.length === 0 && (
        <div className="card"><div className="empty">No categories yet — add the first one above.</div></div>
      )}

      {categories.map((c) => (
        <div className="card" key={c.categoryId}>
          <div className="card-title" style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap' }}>
            <span>
              {c.categoryName}{' '}
              <span className={c.isActive ? 'badge badge-ok' : 'badge'}>{c.isActive ? 'Active' : 'Inactive'}</span>{' '}
              <span className="faint">{c.sourceCount} RAG(s)</span>
            </span>
            {!isEditing('category', c.categoryId) && (
              <span className="row-actions">
                <button type="button" className="btn-link" onClick={() => startEditCategory(c)}>Edit</button>
                <button
                  type="button"
                  className="btn-link btn-link-danger"
                  onClick={() => remove(
                    'category', c.categoryId, c.categoryName,
                    `This also deletes its ${c.subCategories.length} sub categor${c.subCategories.length === 1 ? 'y' : 'ies'} `
                    + `and moves ${c.sourceCount} RAG(s) to "Uncategorised". The RAGs themselves are not deleted.`,
                  )}
                >
                  Delete
                </button>
              </span>
            )}
          </div>

          {isEditing('category', c.categoryId) ? editForm : (
            c.description && <div className="faint" style={{ marginBottom: 8 }}>{c.description}</div>
          )}

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Sub category</th>
                  <th>Description</th>
                  <th className="nowrap">RAGs</th>
                  <th className="nowrap">Status</th>
                  <th className="nowrap">Actions</th>
                </tr>
              </thead>
              <tbody>
                {c.subCategories.length === 0 && (
                  <tr><td colSpan={5} className="empty">No sub categories yet</td></tr>
                )}
                {c.subCategories.map((s) =>
                  isEditing('sub', s.subCategoryId) ? (
                    <tr key={s.subCategoryId}><td colSpan={5}>{editForm}</td></tr>
                  ) : (
                    <tr key={s.subCategoryId}>
                      <td><strong>{s.subCategoryName}</strong></td>
                      <td>{s.description ?? <span className="faint">—</span>}</td>
                      <td className="num nowrap">{s.sourceCount}</td>
                      <td className="nowrap">
                        <span className={s.isActive ? 'badge badge-ok' : 'badge'}>{s.isActive ? 'Active' : 'Inactive'}</span>
                      </td>
                      <td className="nowrap row-actions">
                        <button type="button" className="btn-link" onClick={() => startEditSub(s)}>Edit</button>
                        <button
                          type="button"
                          className="btn-link btn-link-danger"
                          onClick={() => remove(
                            'sub', s.subCategoryId, s.subCategoryName,
                            s.sourceCount > 0 ? `${s.sourceCount} RAG(s) filed here will keep their category but lose the sub category.` : '',
                          )}
                        >
                          Delete
                        </button>
                      </td>
                    </tr>
                  ),
                )}
              </tbody>
            </table>
          </div>

          <form onSubmit={(e) => addSub(e, c)} className="row-actions" style={{ flexWrap: 'wrap', gap: 8, marginTop: 12 }}>
            <input
              value={(newSub[c.categoryId] ?? EMPTY).name}
              onChange={(e) => setNewSub((p) => ({ ...p, [c.categoryId]: { ...(p[c.categoryId] ?? EMPTY), name: e.target.value } }))}
              placeholder="New sub category name"
              required
              maxLength={150}
            />
            <input
              value={(newSub[c.categoryId] ?? EMPTY).description}
              onChange={(e) => setNewSub((p) => ({ ...p, [c.categoryId]: { ...(p[c.categoryId] ?? EMPTY), description: e.target.value } }))}
              placeholder="Description (optional)"
              maxLength={500}
            />
            <button type="submit" className="btn btn-secondary btn-sm" disabled={busy}>Add sub category</button>
          </form>
        </div>
      ))}
    </div>
  );
}
