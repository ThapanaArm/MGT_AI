import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, download, toQuery } from '../api/client';
import {
  FILE_KIND_LABELS,
  PAGE_SIZES,
  POLICY_FLAGS,
  QUESTION_TYPES,
  fileKindIcon,
  formatDateTime,
  formatFileSize,
  formatNumber,
  formatThb,
  policyFlagClass,
  questionTypeLabel,
  toDateInput,
} from '../lib/constants';

const EMPTY_FILTERS = {
  keyword: '',
  userId: '',
  user: '',
  department: '',
  messageRole: '',
  questionType: '',
  chatMode: '',
  policyFlag: '',
  onlyBlocked: false,
  hasAttachments: false,
  sessionId: '',
  from: '',
  to: '',
  sortBy: 'CreatedAt',
  sortDir: 'desc',
  pageSize: 25,
};

/** One-click filter sets — answers "who asked what / where / how" without typing. */
const PRESETS = [
  { label: '"What" questions', patch: { questionType: 'WHAT', messageRole: 'user' } },
  { label: '"Where" questions', patch: { questionType: 'WHERE', messageRole: 'user' } },
  { label: '"How" questions', patch: { questionType: 'HOW', messageRole: 'user' } },
  { label: 'Blocked only', patch: { onlyBlocked: true, messageRole: 'user' } },
  { label: 'With attachments', patch: { hasAttachments: true, messageRole: 'user' } },
  { label: 'Code mode', patch: { chatMode: 'Code' } },
  { label: 'Flagged for monitoring', patch: { policyFlag: 'Warn', messageRole: 'user' } },
  { label: 'Employee questions only', patch: { messageRole: 'user' } },
  {
    label: 'Last 7 days',
    patch: { from: toDateInput(new Date(Date.now() - 6 * 86400000)), to: toDateInput(new Date()) },
  },
];

export default function ChatLogsPage() {
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [applied, setApplied] = useState(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  const [result, setResult] = useState(null);
  const [stats, setStats] = useState(null);
  const [options, setOptions] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [expanded, setExpanded] = useState(() => new Set());

  useEffect(() => {
    api('/api/admin/logs/filters')
      .then(setOptions)
      .catch((err) => setError(err.message));
  }, []);

  const query = useMemo(() => toQuery({ ...applied, page }), [applied, page]);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      const [rows, summary] = await Promise.all([
        api(`/api/admin/logs/chat${query}`),
        api(`/api/admin/logs/chat/stats${toQuery(applied)}`),
      ]);
      setResult(rows);
      setStats(summary);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [query, applied]);

  useEffect(() => {
    load();
  }, [load]);

  function patch(changes) {
    setFilters((prev) => ({ ...prev, ...changes }));
  }

  function search(event) {
    event?.preventDefault();
    setPage(1);
    setApplied(filters);
  }

  function applyPreset(preset) {
    const next = { ...EMPTY_FILTERS, ...preset.patch, pageSize: filters.pageSize };
    setFilters(next);
    setApplied(next);
    setPage(1);
  }

  function reset() {
    setFilters(EMPTY_FILTERS);
    setApplied(EMPTY_FILTERS);
    setPage(1);
  }

  async function handleExport() {
    try {
      await download(`/api/admin/logs/chat/export${toQuery(applied)}`, 'chat-log.csv');
    } catch (err) {
      setError(err.message);
    }
  }

  /** Auditors can open the files employees attached — each open is logged as FILE_DOWNLOADED. */
  async function openAttachment(file) {
    try {
      await download(`/api/chat/attachments/${file.attachmentId}`, file.fileName);
    } catch (err) {
      setError(err.message);
    }
  }

  function toggleExpand(id) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  const maxTypeCount = Math.max(1, ...(stats?.byQuestionType.map((t) => t.count) ?? [1]));
  const maxUserCount = Math.max(1, ...(stats?.byUser.map((u) => u.count) ?? [1]));

  return (
    <div className="page">
      <div className="page-header">
        <h1>Search chat logs</h1>
        <p>
          See which employee asked what, when, from which machine, and which screening rules
          applied — filters stack, so you can combine as many conditions as you need.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      <form className="card" onSubmit={search}>
        <div className="card-title">Filters</div>

        <div className="filter-grid">
          <div>
            <label htmlFor="keyword">Keyword in message</label>
            <input
              id="keyword"
              value={filters.keyword}
              onChange={(e) => patch({ keyword: e.target.value })}
              placeholder='e.g. "where", "price", "INCOTERMS"'
            />
          </div>

          <div>
            <label htmlFor="userId">Employee</label>
            <select
              id="userId"
              value={filters.userId}
              onChange={(e) => patch({ userId: e.target.value })}
            >
              <option value="">— Everyone —</option>
              {options?.users.map((user) => (
                <option key={user.userId} value={user.userId}>
                  {user.fullName} ({user.username})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="department">Department</label>
            <select
              id="department"
              value={filters.department}
              onChange={(e) => patch({ department: e.target.value })}
            >
              <option value="">— All departments —</option>
              {options?.departments.map((dept) => (
                <option key={dept} value={dept}>
                  {dept}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="questionType">Question type</label>
            <select
              id="questionType"
              value={filters.questionType}
              onChange={(e) => patch({ questionType: e.target.value })}
            >
              <option value="">— All types —</option>
              {QUESTION_TYPES.map((type) => (
                <option key={type.value} value={type.value}>
                  {type.label}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="messageRole">Speaker</label>
            <select
              id="messageRole"
              value={filters.messageRole}
              onChange={(e) => patch({ messageRole: e.target.value })}
            >
              <option value="">— Questions and answers —</option>
              <option value="user">Employee questions</option>
              <option value="assistant">AI answers</option>
            </select>
          </div>

          <div>
            <label htmlFor="chatMode">Mode</label>
            <select
              id="chatMode"
              value={filters.chatMode}
              onChange={(e) => patch({ chatMode: e.target.value })}
            >
              <option value="">— All —</option>
              <option value="Chat">Chat</option>
              <option value="Code">Code</option>
            </select>
          </div>

          <div>
            <label htmlFor="policyFlag">Policy flag</label>
            <select
              id="policyFlag"
              value={filters.policyFlag}
              onChange={(e) => patch({ policyFlag: e.target.value })}
            >
              <option value="">— All —</option>
              {POLICY_FLAGS.map((flag) => (
                <option key={flag.value} value={flag.value}>
                  {flag.label}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="from">From date</label>
            <input
              id="from"
              type="date"
              value={filters.from}
              onChange={(e) => patch({ from: e.target.value })}
            />
          </div>

          <div>
            <label htmlFor="to">To date</label>
            <input
              id="to"
              type="date"
              value={filters.to}
              onChange={(e) => patch({ to: e.target.value })}
            />
          </div>

          <div>
            <label htmlFor="sortBy">Sort by</label>
            <select id="sortBy" value={filters.sortBy} onChange={(e) => patch({ sortBy: e.target.value })}>
              <option value="CreatedAt">Timestamp</option>
              <option value="Username">Username</option>
              <option value="QuestionType">Question type</option>
              <option value="OutputTokens">Answer tokens</option>
              <option value="LatencyMs">AI response time</option>
            </select>
          </div>

          <div>
            <label htmlFor="sortDir">Direction</label>
            <select id="sortDir" value={filters.sortDir} onChange={(e) => patch({ sortDir: e.target.value })}>
              <option value="desc">High → low / newest → oldest</option>
              <option value="asc">Low → high / oldest → newest</option>
            </select>
          </div>

          <div>
            <label htmlFor="pageSize">Rows per page</label>
            <select
              id="pageSize"
              value={filters.pageSize}
              onChange={(e) => patch({ pageSize: Number(e.target.value) })}
            >
              {PAGE_SIZES.map((size) => (
                <option key={size} value={size}>
                  {size}
                </option>
              ))}
            </select>
          </div>

          <div style={{ display: 'flex', alignItems: 'flex-end', paddingBottom: 8 }}>
            <label className="checkbox">
              <input
                type="checkbox"
                checked={filters.onlyBlocked}
                onChange={(e) => patch({ onlyBlocked: e.target.checked })}
              />
              Blocked messages only
            </label>
          </div>

          <div style={{ display: 'flex', alignItems: 'flex-end', paddingBottom: 8 }}>
            <label className="checkbox">
              <input
                type="checkbox"
                checked={filters.hasAttachments}
                onChange={(e) => patch({ hasAttachments: e.target.checked })}
              />
              With attachments only
            </label>
          </div>
        </div>

        <div className="filter-actions">
          <button type="submit" className="btn" disabled={loading}>
            {loading ? 'Searching…' : 'Search'}
          </button>
          <button type="button" className="btn btn-secondary" onClick={reset}>
            Clear filters
          </button>
          <span className="spacer" />
          <button type="button" className="btn btn-secondary" onClick={handleExport}>
            Download CSV
          </button>
        </div>

        <div className="filter-actions" style={{ borderTop: 'none', paddingTop: 4, marginTop: 4 }}>
          <span className="muted" style={{ fontSize: 13 }}>Common filters:</span>
          {PRESETS.map((preset) => (
            <button
              key={preset.label}
              type="button"
              className="chat-suggestion"
              onClick={() => applyPreset(preset)}
            >
              {preset.label}
            </button>
          ))}
        </div>
      </form>

      {stats && (
        <>
          <div className="stat-grid">
            <div className="stat">
              <div className="stat-label">Total messages</div>
              <div className="stat-value">{formatNumber(stats.totalMessages)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">Employee questions</div>
              <div className="stat-value">{formatNumber(stats.questionCount)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">AI answers</div>
              <div className="stat-value">{formatNumber(stats.answerCount)}</div>
            </div>
            <div className="stat danger">
              <div className="stat-label">Blocked</div>
              <div className="stat-value">{formatNumber(stats.blockedCount)}</div>
            </div>
            <div className="stat warn">
              <div className="stat-label">Flagged</div>
              <div className="stat-value">{formatNumber(stats.flaggedCount)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">Active employees</div>
              <div className="stat-value">{formatNumber(stats.distinctUsers)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">Conversations</div>
              <div className="stat-value">{formatNumber(stats.distinctSessions)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">Tokens (in/out)</div>
              <div className="stat-value" style={{ fontSize: 16 }}>
                {formatNumber(stats.totalInputTokens)} / {formatNumber(stats.totalOutputTokens)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Total tokens</div>
              <div className="stat-value" style={{ fontSize: 18 }}>
                {formatNumber(stats.totalTokens)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Total cost</div>
              <div className="stat-value" style={{ fontSize: 18, color: 'var(--brand-dark)' }}>
                {formatThb(stats.totalCostThb, { compact: true })} THB
              </div>
            </div>
          </div>

          <div className="stat-columns">
            <div className="card">
              <div className="card-title">By question type</div>
              {stats.byQuestionType.length === 0 && <div className="empty">No data</div>}
              <div className="bar-list">
                {stats.byQuestionType.map((item) => (
                  <div className="bar-row" key={item.key}>
                    <span>{item.label}</span>
                    <span className="bar-track">
                      <span
                        className="bar-fill"
                        style={{ width: `${(item.count / maxTypeCount) * 100}%` }}
                      />
                    </span>
                    <span className="bar-count">{formatNumber(item.count)}</span>
                  </div>
                ))}
              </div>
            </div>

            <div className="card">
              <div className="card-title">Most active employees</div>
              {stats.byUser.length === 0 && <div className="empty">No data</div>}
              <div className="bar-list">
                {stats.byUser.map((item) => (
                  <div className="bar-row" key={item.key}>
                    <span>{item.label}</span>
                    <span className="bar-track">
                      <span
                        className="bar-fill"
                        style={{ width: `${(item.count / maxUserCount) * 100}%` }}
                      />
                    </span>
                    <span className="bar-count">{formatNumber(item.count)}</span>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </>
      )}

      <div className="card">
        <div className="card-title">
          Results
          <span className="muted" style={{ fontWeight: 400, fontSize: 13 }}>
            {result ? `${formatNumber(result.totalCount)} match(es)` : ''}
          </span>
        </div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="nowrap">Timestamp</th>
                <th className="nowrap">Employee</th>
                <th className="nowrap">Dept.</th>
                <th className="nowrap">Type</th>
                <th>Content</th>
                <th className="nowrap">Mode</th>
                <th className="nowrap">Attachments</th>
                <th className="nowrap">Policy</th>
                <th className="num nowrap">Tokens</th>
                <th className="num nowrap">Cost (THB)</th>
                <th className="num nowrap">ms</th>
                <th className="nowrap">IP</th>
              </tr>
            </thead>
            <tbody>
              {result?.items.length === 0 && (
                <tr>
                  <td colSpan={12} className="empty">
                    No records match these filters
                  </td>
                </tr>
              )}

              {result?.items.map((item) => {
                const isLong = item.content.length > 180;
                const isOpen = expanded.has(item.messageId);

                return (
                  <tr key={item.messageId}>
                    <td className="nowrap">{formatDateTime(item.createdAt)}</td>
                    <td className="nowrap">
                      {item.fullName}
                      <div className="faint">{item.username}</div>
                    </td>
                    <td className="nowrap">{item.department ?? '—'}</td>
                    <td className="nowrap">
                      {item.messageRole === 'user' ? (
                        <span className="badge badge-brand">{questionTypeLabel(item.questionType)}</span>
                      ) : (
                        <span className="badge">AI answer</span>
                      )}
                    </td>
                    <td className="cell-content">
                      {isLong && !isOpen ? `${item.content.slice(0, 180)}…` : item.content}
                      {isLong && (
                        <>
                          {' '}
                          <button
                            type="button"
                            className="btn-link"
                            onClick={() => toggleExpand(item.messageId)}
                          >
                            {isOpen ? 'Show less' : 'Show all'}
                          </button>
                        </>
                      )}
                    </td>
                    <td className="nowrap">
                      {item.chatMode === 'Code' ? (
                        <span className="badge badge-info">Code</span>
                      ) : (
                        <span className="badge">Chat</span>
                      )}
                      {item.modelName && <div className="faint">{item.modelName}</div>}
                    </td>
                    <td className="nowrap">
                      {item.attachmentCount === 0 ? (
                        <span className="faint">—</span>
                      ) : (
                        <div className="file-list">
                          {item.attachments.map((file) => (
                            <button
                              key={file.attachmentId}
                              type="button"
                              className="file-chip"
                              title={
                                `${FILE_KIND_LABELS[file.fileKind] ?? file.fileKind} · ` +
                                `${formatFileSize(file.sizeBytes)} · sha256 ${file.sha256.slice(0, 12)}… · ` +
                                (file.policyScanned
                                  ? 'content screened'
                                  : 'file name screened only') +
                                ' — click to download'
                              }
                              onClick={() => openAttachment(file)}
                            >
                              <span>{fileKindIcon(file.fileKind)}</span>
                              <span className="file-chip-name">{file.fileName}</span>
                              {!file.policyScanned && (
                                <span className="badge badge-warn" style={{ fontSize: 10 }}>
                                  not scanned
                                </span>
                              )}
                            </button>
                          ))}
                        </div>
                      )}
                    </td>
                    <td className="nowrap">
                      {item.isBlocked && <span className="badge badge-danger">Blocked</span>}
                      {!item.isBlocked && item.policyFlag && (
                        <span className={policyFlagClass(item.policyFlag)}>{item.policyFlag}</span>
                      )}
                      {item.policyRuleName && <div className="faint">{item.policyRuleName}</div>}
                      {!item.isBlocked && !item.policyFlag && <span className="faint">—</span>}
                    </td>
                    <td className="num nowrap">
                      {item.outputTokens == null ? (
                        '—'
                      ) : (
                        <>
                          {formatNumber(item.inputTokens)}/{formatNumber(item.outputTokens)}
                          {(item.cacheWriteTokens > 0 || item.cacheReadTokens > 0) && (
                            <div className="faint">
                              cache {formatNumber(item.cacheWriteTokens)}/
                              {formatNumber(item.cacheReadTokens)}
                            </div>
                          )}
                        </>
                      )}
                    </td>
                    <td className="num nowrap">
                      {item.totalCostThb == null ? (
                        '—'
                      ) : (
                        <>
                          <strong>{formatThb(item.totalCostThb)}</strong>
                          <div className="faint">${item.totalCostUsd?.toFixed(6)}</div>
                        </>
                      )}
                    </td>
                    <td className="num nowrap">{item.latencyMs == null ? '—' : formatNumber(item.latencyMs)}</td>
                    <td className="nowrap mono">{item.clientIp ?? '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {result && result.totalPages > 1 && (
          <div className="pager">
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </button>
            <span>
              Page {result.page} of {result.totalPages}
            </span>
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              disabled={page >= result.totalPages}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
