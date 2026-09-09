import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, toQuery } from '../api/client';
import { AUDIT_CATEGORIES, PAGE_SIZES, formatDateTime, formatNumber } from '../lib/constants';

const EMPTY_FILTERS = {
  keyword: '',
  userId: '',
  category: '',
  action: '',
  onlyFailed: false,
  from: '',
  to: '',
  pageSize: 50,
};

export default function AuditLogsPage() {
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [applied, setApplied] = useState(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  const [result, setResult] = useState(null);
  const [options, setOptions] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

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
      setResult(await api(`/api/admin/logs/audit${query}`));
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [query]);

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

  function reset() {
    setFilters(EMPTY_FILTERS);
    setApplied(EMPTY_FILTERS);
    setPage(1);
  }

  return (
    <div className="page">
      <div className="page-header">
        <h1>System event log</h1>
        <p>
          Every significant event: sign in and out, failed sign-in attempts, blocked messages,
          log searches, and changes to users, screening rules and pricing.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      <form className="card" onSubmit={search}>
        <div className="card-title">Filters</div>

        <div className="filter-grid">
          <div>
            <label htmlFor="keyword">Keyword (detail / event name)</label>
            <input
              id="keyword"
              value={filters.keyword}
              onChange={(e) => patch({ keyword: e.target.value })}
              placeholder="e.g. password, LOGIN"
            />
          </div>

          <div>
            <label htmlFor="userId">User</label>
            <select id="userId" value={filters.userId} onChange={(e) => patch({ userId: e.target.value })}>
              <option value="">— Everyone —</option>
              {options?.users.map((user) => (
                <option key={user.userId} value={user.userId}>
                  {user.fullName} ({user.username})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="category">Category</label>
            <select id="category" value={filters.category} onChange={(e) => patch({ category: e.target.value })}>
              <option value="">— All categories —</option>
              {AUDIT_CATEGORIES.map((cat) => (
                <option key={cat.value} value={cat.value}>
                  {cat.label}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="action">Event</label>
            <select id="action" value={filters.action} onChange={(e) => patch({ action: e.target.value })}>
              <option value="">— All events —</option>
              {options?.auditActions.map((action) => (
                <option key={action} value={action}>
                  {action}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="from">From date</label>
            <input id="from" type="date" value={filters.from} onChange={(e) => patch({ from: e.target.value })} />
          </div>

          <div>
            <label htmlFor="to">To date</label>
            <input id="to" type="date" value={filters.to} onChange={(e) => patch({ to: e.target.value })} />
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
                checked={filters.onlyFailed}
                onChange={(e) => patch({ onlyFailed: e.target.checked })}
              />
              Failed events only
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
          <span className="muted">{result ? `${formatNumber(result.totalCount)} match(es)` : ''}</span>
        </div>
      </form>

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th className="nowrap">Timestamp</th>
              <th className="nowrap">User</th>
              <th className="nowrap">Category</th>
              <th className="nowrap">Event</th>
              <th className="nowrap">Result</th>
              <th>Detail</th>
              <th className="nowrap">IP</th>
            </tr>
          </thead>
          <tbody>
            {result?.items.length === 0 && (
              <tr>
                <td colSpan={7} className="empty">
                  No records match these filters
                </td>
              </tr>
            )}

            {result?.items.map((item) => (
              <tr key={item.auditId}>
                <td className="nowrap">{formatDateTime(item.createdAt)}</td>
                <td className="nowrap">{item.username ?? '—'}</td>
                <td className="nowrap">
                  <span className="badge">{item.category}</span>
                </td>
                <td className="nowrap mono">{item.action}</td>
                <td className="nowrap">
                  <span className={item.isSuccess ? 'badge badge-ok' : 'badge badge-danger'}>
                    {item.isSuccess ? 'Success' : 'Failed'}
                  </span>
                </td>
                <td className="cell-content">{item.detail ?? '—'}</td>
                <td className="nowrap mono">{item.clientIp ?? '—'}</td>
              </tr>
            ))}
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
  );
}
