import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDateTime, policyFlagClass, questionTypeLabel } from '../lib/constants';

/** Must split the same way PolicyService.SplitKeywords does, so the preview matches reality. */
function splitKeywords(pattern) {
  const seen = new Set();
  const result = [];

  for (const raw of pattern.split(/[,|\r\n]/)) {
    const keyword = raw.trim();
    if (!keyword) continue;

    const key = keyword.toLowerCase();
    if (seen.has(key)) continue;

    seen.add(key);
    result.push(keyword);
  }

  return result;
}

const EMPTY_RULE = {
  ruleName: '',
  description: '',
  matchType: 'Keyword',
  pattern: '',
  actionType: 'Warn',
  severity: 'Medium',
  isActive: true,
};

export default function PolicyRulesPage() {
  const [rules, setRules] = useState([]);
  const [form, setForm] = useState(EMPTY_RULE);
  const [editingId, setEditingId] = useState(null);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);

  const [testText, setTestText] = useState('');
  const [testResult, setTestResult] = useState(null);

  const load = useCallback(async () => {
    try {
      setRules(await api('/api/admin/policy-rules'));
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

  function startEdit(rule) {
    setEditingId(rule.ruleId);
    setForm({
      ruleName: rule.ruleName,
      description: rule.description ?? '',
      matchType: rule.matchType,
      pattern: rule.pattern,
      actionType: rule.actionType,
      severity: rule.severity,
      isActive: rule.isActive,
    });
    setError(null);
    setMessage(null);
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(EMPTY_RULE);
    setError(null);
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    try {
      if (editingId) {
        await api(`/api/admin/policy-rules/${editingId}`, { method: 'PUT', body: form });
        setMessage(`Saved changes to the rule "${form.ruleName}"`);
      } else {
        await api('/api/admin/policy-rules', { method: 'POST', body: form });
        setMessage(`Added the rule "${form.ruleName}"`);
      }
      cancelEdit();
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete(rule) {
    if (!window.confirm(`Delete the rule "${rule.ruleName}"?`)) return;

    try {
      await api(`/api/admin/policy-rules/${rule.ruleId}`, { method: 'DELETE' });
      setMessage(`Deleted the rule "${rule.ruleName}"`);
      if (editingId === rule.ruleId) cancelEdit();
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  async function toggleActive(rule) {
    try {
      await api(`/api/admin/policy-rules/${rule.ruleId}`, {
        method: 'PUT',
        body: {
          ruleName: rule.ruleName,
          description: rule.description ?? '',
          matchType: rule.matchType,
          pattern: rule.pattern,
          actionType: rule.actionType,
          severity: rule.severity,
          isActive: !rule.isActive,
        },
      });
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  async function handleTest(event) {
    event.preventDefault();
    setError(null);

    try {
      setTestResult(await api('/api/admin/policy-rules/test', {
        method: 'POST',
        body: { message: testText },
      }));
    } catch (err) {
      setError(err.message);
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>Question screening rules</h1>
        <p>
          Decide which messages must never reach the AI (Block), which may go through with a
          warning (Warn), and which pass normally but are flagged for auditors (Audit).
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="stat-columns">
        <form className="card" onSubmit={handleSubmit}>
          <div className="card-title">{editingId ? `Edit rule #${editingId}` : 'Add a rule'}</div>

          <div className="field">
            <label htmlFor="ruleName">Rule name</label>
            <input
              id="ruleName"
              value={form.ruleName}
              onChange={(e) => patch({ ruleName: e.target.value })}
              required
              maxLength={200}
            />
          </div>

          <div className="field">
            <label htmlFor="description">Description</label>
            <textarea
              id="description"
              value={form.description}
              onChange={(e) => patch({ description: e.target.value })}
              rows={2}
              maxLength={500}
            />
          </div>

          <div className="form-grid">
            <div className="field">
              <label htmlFor="matchType">Match type</label>
              <select id="matchType" value={form.matchType} onChange={(e) => patch({ matchType: e.target.value })}>
                <option value="Keyword">Keyword — plain terms</option>
                <option value="Regex">Regex — .NET pattern</option>
              </select>
            </div>

            <div className="field">
              <label htmlFor="actionType">Action</label>
              <select id="actionType" value={form.actionType} onChange={(e) => patch({ actionType: e.target.value })}>
                <option value="Block">Block — never sent to the AI</option>
                <option value="Warn">Warn — sent, user warned</option>
                <option value="Audit">Audit — flagged silently</option>
              </select>
            </div>

            <div className="field">
              <label htmlFor="severity">Severity</label>
              <select id="severity" value={form.severity} onChange={(e) => patch({ severity: e.target.value })}>
                <option value="High">High</option>
                <option value="Medium">Medium</option>
                <option value="Low">Low</option>
              </select>
            </div>
          </div>

          <div className="field">
            <label htmlFor="pattern">
              {form.matchType === 'Regex' ? 'Regex pattern' : 'Terms to detect (several allowed)'}
            </label>
            <input
              id="pattern"
              value={form.pattern}
              onChange={(e) => patch({ pattern: e.target.value })}
              className={form.matchType === 'Regex' ? 'mono' : undefined}
              placeholder={form.matchType === 'Regex' ? '' : 'salary, payroll, เงินเดือน'}
              required
              maxLength={500}
            />
            <div className="faint" style={{ fontSize: 12, marginTop: 4 }}>
              {form.matchType === 'Regex' ? (
                'Example: (?i)(password\\s*[:=]|api[_\\s-]?key)'
              ) : (
                <>
                  Enter several terms <strong>separated by commas</strong> (or <code>|</code>) — a
                  message matches when <strong>any one</strong> term appears, not all of them.
                  Matching is case-insensitive and looks for the term anywhere in the text. Mix
                  Thai and English terms so both languages are covered. If a term itself contains
                  a comma, use Regex instead.
                </>
              )}
            </div>
            {form.matchType === 'Keyword' && form.pattern.trim() && (
              <div style={{ fontSize: 12, marginTop: 6 }}>
                Will detect {splitKeywords(form.pattern).length} term(s):{' '}
                {splitKeywords(form.pattern).map((k) => (
                  <span key={k} className="badge" style={{ marginRight: 4 }}>
                    {k}
                  </span>
                ))}
              </div>
            )}
          </div>

          <div className="field">
            <label className="checkbox">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => patch({ isActive: e.target.checked })}
              />
              Enable this rule
            </label>
          </div>

          <div className="row-actions">
            <button type="submit" className="btn" disabled={busy}>
              {editingId ? 'Save changes' : 'Add rule'}
            </button>
            {editingId && (
              <button type="button" className="btn btn-secondary" onClick={cancelEdit}>
                Cancel
              </button>
            )}
          </div>
        </form>

        <form className="card" onSubmit={handleTest}>
          <div className="card-title">Test a message</div>
          <p className="muted" style={{ marginTop: -8, fontSize: 13 }}>
            See which rule a message would match, without calling the AI and without writing to
            the chat log.
          </p>

          <div className="field">
            <textarea
              value={testText}
              onChange={(e) => setTestText(e.target.value)}
              rows={3}
              placeholder="e.g. What is the SAP system password?"
              required
            />
          </div>

          <button type="submit" className="btn btn-secondary" disabled={!testText.trim()}>
            Test
          </button>

          {testResult && (
            <div
              className={`alert ${testResult.flag === 'Block' ? 'alert-danger' : testResult.flag ? 'alert-warn' : 'alert-ok'}`}
              style={{ marginTop: 14 }}
            >
              <div>
                <strong>Screening result:</strong>{' '}
                {testResult.flag ? (
                  <>
                    <span className={policyFlagClass(testResult.flag)}>{testResult.flag}</span>{' '}
                    from rule #{testResult.ruleId} &quot;{testResult.ruleName}&quot; (severity{' '}
                    {testResult.severity})
                  </>
                ) : (
                  'Passed — no rule matched'
                )}
              </div>
              <div style={{ marginTop: 4 }}>
                <strong>Detected question type:</strong> {questionTypeLabel(testResult.questionType)}
              </div>
              {testResult.notice && <div style={{ marginTop: 4 }}>{testResult.notice}</div>}
            </div>
          )}
        </form>
      </div>

      <div className="card">
        <div className="card-title">All rules ({rules.length})</div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="num">#</th>
                <th>Rule name</th>
                <th className="nowrap">Match type</th>
                <th>Pattern / terms</th>
                <th className="nowrap">Action</th>
                <th className="nowrap">Severity</th>
                <th className="nowrap">Status</th>
                <th className="nowrap">Last changed</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {rules.length === 0 && (
                <tr>
                  <td colSpan={9} className="empty">
                    No rules configured yet
                  </td>
                </tr>
              )}

              {rules.map((rule) => (
                <tr key={rule.ruleId}>
                  <td className="num">{rule.ruleId}</td>
                  <td>
                    {rule.ruleName}
                    {rule.description && <div className="faint">{rule.description}</div>}
                  </td>
                  <td className="nowrap">
                    <span className="badge">{rule.matchType}</span>
                  </td>
                  <td className="mono cell-content">{rule.pattern}</td>
                  <td className="nowrap">
                    <span className={policyFlagClass(rule.actionType)}>{rule.actionType}</span>
                  </td>
                  <td className="nowrap">{rule.severity}</td>
                  <td className="nowrap">
                    <span className={rule.isActive ? 'badge badge-ok' : 'badge'}>
                      {rule.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="nowrap faint">
                    {rule.updatedAt ? formatDateTime(rule.updatedAt) : formatDateTime(rule.createdAt)}
                    <div>{rule.updatedBy ?? rule.createdBy ?? ''}</div>
                  </td>
                  <td className="nowrap">
                    <div className="row-actions">
                      <button type="button" className="btn btn-secondary btn-sm" onClick={() => startEdit(rule)}>
                        Edit
                      </button>
                      <button type="button" className="btn btn-secondary btn-sm" onClick={() => toggleActive(rule)}>
                        {rule.isActive ? 'Disable' : 'Enable'}
                      </button>
                      <button type="button" className="btn btn-danger btn-sm" onClick={() => handleDelete(rule)}>
                        Delete
                      </button>
                    </div>
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
