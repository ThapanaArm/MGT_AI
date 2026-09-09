import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, toQuery } from '../api/client';
import { formatNumber, formatThb, formatUsd, toDateInput } from '../lib/constants';

const EMPTY_FILTERS = { userId: '', department: '', from: '', to: '' };

const RANGES = [
  { label: 'Today', days: 0 },
  { label: 'Last 7 days', days: 6 },
  { label: 'Last 30 days', days: 29 },
  { label: 'All time', days: null },
];

/** One breakdown table (per user / per model / per department / per day). */
function CostTable({ title, buckets, keyLabel, total }) {
  const max = Math.max(1, ...buckets.map((b) => b.totalCostThb));

  return (
    <div className="card">
      <div className="card-title">{title}</div>

      {buckets.length === 0 ? (
        <div className="empty">No data in the selected range</div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{keyLabel}</th>
                <th className="num nowrap">Messages</th>
                <th className="num nowrap">Tokens in</th>
                <th className="num nowrap">Tokens out</th>
                <th className="num nowrap">Total tokens</th>
                <th className="num nowrap">USD</th>
                <th className="num nowrap">THB</th>
                <th className="nowrap" style={{ minWidth: 120 }}>Share</th>
              </tr>
            </thead>
            <tbody>
              {buckets.map((b) => (
                <tr key={b.key}>
                  <td>{b.label}</td>
                  <td className="num">{formatNumber(b.messageCount)}</td>
                  <td className="num">{formatNumber(b.inputTokens)}</td>
                  <td className="num">{formatNumber(b.outputTokens)}</td>
                  <td className="num">{formatNumber(b.totalTokens)}</td>
                  <td className="num">{formatUsd(b.totalCostUsd)}</td>
                  <td className="num">
                    <strong>{formatThb(b.totalCostThb)}</strong>
                  </td>
                  <td>
                    <span className="bar-track">
                      <span
                        className="bar-fill"
                        style={{ width: `${(b.totalCostThb / max) * 100}%` }}
                      />
                    </span>
                    <span className="faint" style={{ fontSize: 11 }}>
                      {total > 0 ? `${((b.totalCostThb / total) * 100).toFixed(1)}%` : '—'}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export default function CostReportPage() {
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [applied, setApplied] = useState(EMPTY_FILTERS);
  const [report, setReport] = useState(null);
  const [options, setOptions] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    api('/api/admin/logs/filters')
      .then(setOptions)
      .catch((err) => setError(err.message));
  }, []);

  const query = useMemo(() => toQuery(applied), [applied]);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setReport(await api(`/api/admin/logs/cost${query}`));
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [query]);

  useEffect(() => {
    load();
  }, [load]);

  function applyRange(range) {
    const next =
      range.days === null
        ? { ...filters, from: '', to: '' }
        : {
            ...filters,
            from: toDateInput(new Date(Date.now() - range.days * 86400000)),
            to: toDateInput(new Date()),
          };
    setFilters(next);
    setApplied(next);
  }

  const total = report?.total;

  return (
    <div className="page">
      <div className="page-header">
        <h1>Token cost report</h1>
        <p>
          Costs are calculated and written to the database every time the AI answers, so past
          figures never change even if prices or the exchange rate are updated later.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      <form
        className="card"
        onSubmit={(e) => {
          e.preventDefault();
          setApplied(filters);
        }}
      >
        <div className="card-title">Data range</div>

        <div className="filter-grid">
          <div>
            <label htmlFor="userId">Employee</label>
            <select
              id="userId"
              value={filters.userId}
              onChange={(e) => setFilters({ ...filters, userId: e.target.value })}
            >
              <option value="">— Everyone —</option>
              {options?.users.map((u) => (
                <option key={u.userId} value={u.userId}>
                  {u.fullName} ({u.username})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="department">Department</label>
            <select
              id="department"
              value={filters.department}
              onChange={(e) => setFilters({ ...filters, department: e.target.value })}
            >
              <option value="">— All departments —</option>
              {options?.departments.map((d) => (
                <option key={d} value={d}>
                  {d}
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
              onChange={(e) => setFilters({ ...filters, from: e.target.value })}
            />
          </div>

          <div>
            <label htmlFor="to">To date</label>
            <input
              id="to"
              type="date"
              value={filters.to}
              onChange={(e) => setFilters({ ...filters, to: e.target.value })}
            />
          </div>
        </div>

        <div className="filter-actions">
          <button type="submit" className="btn" disabled={loading}>
            {loading ? 'Calculating…' : 'Show report'}
          </button>
          <button
            type="button"
            className="btn btn-secondary"
            onClick={() => {
              setFilters(EMPTY_FILTERS);
              setApplied(EMPTY_FILTERS);
            }}
          >
            Clear filters
          </button>
          <span className="spacer" />
          {RANGES.map((r) => (
            <button
              key={r.label}
              type="button"
              className="chat-suggestion"
              onClick={() => applyRange(r)}
            >
              {r.label}
            </button>
          ))}
        </div>
      </form>

      {total && (
        <>
          <div className="stat-grid">
            <div className="stat">
              <div className="stat-label">Total cost (THB)</div>
              <div className="stat-value" style={{ color: 'var(--brand-dark)' }}>
                {formatThb(total.totalCostThb, { compact: true })}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Total cost (USD)</div>
              <div className="stat-value" style={{ fontSize: 18 }}>
                ${formatUsd(total.totalCostUsd)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">AI answers</div>
              <div className="stat-value">{formatNumber(total.messageCount)}</div>
            </div>
            <div className="stat">
              <div className="stat-label">Total tokens</div>
              <div className="stat-value" style={{ fontSize: 18 }}>
                {formatNumber(total.totalTokens)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Tokens in / out</div>
              <div className="stat-value" style={{ fontSize: 15 }}>
                {formatNumber(total.inputTokens)} / {formatNumber(total.outputTokens)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Cache write / read</div>
              <div className="stat-value" style={{ fontSize: 15 }}>
                {formatNumber(total.cacheWriteTokens)} / {formatNumber(total.cacheReadTokens)}
              </div>
            </div>
            <div className="stat">
              <div className="stat-label">Average per answer</div>
              <div className="stat-value" style={{ fontSize: 18 }}>
                {formatThb(report.averageCostThbPerQuestion)} THB
              </div>
            </div>
            <div className="stat warn">
              <div className="stat-label">Highest single answer</div>
              <div className="stat-value" style={{ fontSize: 18 }}>
                {formatThb(report.maxCostThbSingleMessage)} THB
              </div>
            </div>
          </div>

          {total.messageCount === 0 && (
            <div className="alert alert-info">
              No billable AI answers in this range — messages blocked by policy are never sent to
              the AI, so they cost nothing.
            </div>
          )}

          <CostTable
            title="By employee"
            keyLabel="Employee"
            buckets={report.byUser}
            total={total.totalCostThb}
          />
          <CostTable
            title="By department"
            keyLabel="Department"
            buckets={report.byDepartment}
            total={total.totalCostThb}
          />
          <CostTable
            title="By model"
            keyLabel="Model"
            buckets={report.byModel}
            total={total.totalCostThb}
          />
          <CostTable
            title="By day"
            keyLabel="Date"
            buckets={report.byDay}
            total={total.totalCostThb}
          />
        </>
      )}
    </div>
  );
}
