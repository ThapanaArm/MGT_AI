import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDateTime, formatNumber, formatThb } from '../lib/constants';

/** สีป้ายแยกผู้ให้บริการ ให้กวาดตาหาแถวของเจ้าที่ต้องการได้เร็วในตารางที่ยาวขึ้นเรื่อย ๆ */
const PROVIDER_BADGE = {
  Anthropic: 'badge badge-brand',
  Google: 'badge badge-info',
  OpenAI: 'badge badge-ok',
};

const EMPTY_FORM = {
  provider: 'Anthropic',
  modelName: '',
  inputUsdPerMTok: 5,
  outputUsdPerMTok: 25,
  cacheWriteUsdPerMTok: 6.25,
  cacheReadUsdPerMTok: 0.5,
  usdToThbRate: 36.5,
  notes: '',
  isActive: true,
};

export default function ModelPricingPage() {
  const [rows, setRows] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editingId, setEditingId] = useState(null);
  const [error, setError] = useState(null);
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setRows(await api('/api/admin/model-pricing'));
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

  /** Cache prices follow Anthropic's formula from the input price — filled in to avoid typos. */
  function syncCacheFromInput(inputPrice) {
    const value = Number(inputPrice);
    if (!Number.isFinite(value)) return;

    patch({
      inputUsdPerMTok: inputPrice,
      cacheWriteUsdPerMTok: Number((value * 1.25).toFixed(4)),
      cacheReadUsdPerMTok: Number((value * 0.1).toFixed(4)),
    });
  }

  function startEdit(row) {
    setEditingId(row.pricingId);
    setForm({
      provider: row.provider ?? 'Anthropic',
      modelName: row.modelName,
      inputUsdPerMTok: row.inputUsdPerMTok,
      outputUsdPerMTok: row.outputUsdPerMTok,
      cacheWriteUsdPerMTok: row.cacheWriteUsdPerMTok,
      cacheReadUsdPerMTok: row.cacheReadUsdPerMTok,
      usdToThbRate: row.usdToThbRate,
      notes: row.notes ?? '',
      isActive: row.isActive,
    });
    setError(null);
    setMessage(null);
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(EMPTY_FORM);
    setError(null);
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setMessage(null);
    setBusy(true);

    const body = {
      ...form,
      provider: form.provider,
      inputUsdPerMTok: Number(form.inputUsdPerMTok),
      outputUsdPerMTok: Number(form.outputUsdPerMTok),
      cacheWriteUsdPerMTok: Number(form.cacheWriteUsdPerMTok),
      cacheReadUsdPerMTok: Number(form.cacheReadUsdPerMTok),
      usdToThbRate: Number(form.usdToThbRate),
    };

    try {
      if (editingId) {
        await api(`/api/admin/model-pricing/${editingId}`, { method: 'PUT', body });
        setMessage(
          `Pricing for ${body.modelName} saved — this applies to messages from now on only. ` +
            'Costs already recorded do not change.',
        );
      } else {
        await api('/api/admin/model-pricing', { method: 'POST', body });
        setMessage(`Pricing for ${body.modelName} added`);
      }
      cancelEdit();
      await load();
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  /** Sample cost for a single question, so the effect of a price is visible before saving. */
  const sample = (() => {
    const input = Number(form.inputUsdPerMTok) || 0;
    const output = Number(form.outputUsdPerMTok) || 0;
    const rate = Number(form.usdToThbRate) || 0;
    const usd = (700 / 1e6) * input + (600 / 1e6) * output;
    return { usd, thb: usd * rate };
  })();

  return (
    <div className="page">
      <div className="page-header">
        <h1>Model pricing and exchange rate</h1>
        <p>
          Used to calculate the cost of each answer, which is then written straight to the
          database. Editing a price here affects messages from now on only.
        </p>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {message && <div className="alert alert-ok">{message}</div>}

      <div className="alert alert-info">
        The seeded prices follow Anthropic's published rates (USD per 1M tokens), with cache write
        = input × 1.25 and cache read = input × 0.1. Check the current rates on Anthropic's
        pricing page and set the exchange rate your finance team uses before treating these
        figures as real money.
      </div>

      <div className="card">
        <div className="card-title">Configured prices ({rows.length} models)</div>

        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="nowrap">Provider</th>
                <th>Model</th>
                <th className="num nowrap">Input $/1M</th>
                <th className="num nowrap">Output $/1M</th>
                <th className="num nowrap">Cache write $/1M</th>
                <th className="num nowrap">Cache read $/1M</th>
                <th className="num nowrap">THB per USD</th>
                <th className="nowrap">Status</th>
                <th className="num nowrap">Actual use</th>
                <th className="nowrap">Last changed</th>
                <th className="nowrap">Actions</th>
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 && (
                <tr>
                  <td colSpan={11} className="empty">
                    No pricing configured yet
                  </td>
                </tr>
              )}

              {rows.map((row) => (
                <tr key={row.pricingId}>
                  <td className="nowrap">
                    <span className={PROVIDER_BADGE[row.provider] ?? 'badge'}>
                      {row.provider ?? 'Anthropic'}
                    </span>
                  </td>
                  <td className="nowrap">
                    <strong>{row.modelName}</strong>
                    {row.notes && <div className="faint">{row.notes}</div>}
                  </td>
                  <td className="num">{row.inputUsdPerMTok}</td>
                  <td className="num">{row.outputUsdPerMTok}</td>
                  <td className="num">{row.cacheWriteUsdPerMTok}</td>
                  <td className="num">{row.cacheReadUsdPerMTok}</td>
                  <td className="num">{row.usdToThbRate}</td>
                  <td className="nowrap">
                    <span className={row.isActive ? 'badge badge-ok' : 'badge'}>
                      {row.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="num nowrap">
                    {formatNumber(row.messageCount)} messages
                    <div className="faint">{formatThb(row.totalCostThb)} THB</div>
                  </td>
                  <td className="nowrap faint">
                    {row.updatedAt ? formatDateTime(row.updatedAt) : formatDateTime(row.createdAt)}
                    <div>{row.updatedBy ?? row.createdBy ?? ''}</div>
                  </td>
                  <td className="nowrap">
                    <button
                      type="button"
                      className="btn btn-secondary btn-sm"
                      onClick={() => startEdit(row)}
                    >
                      Edit
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <form className="card narrow" onSubmit={handleSubmit}>
        <div className="card-title">
          {editingId ? `Edit pricing #${editingId}` : 'Add pricing for a model'}
        </div>

        <div className="field">
          <label htmlFor="provider">AI provider</label>
          <select
            id="provider"
            value={form.provider}
            onChange={(e) => patch({ provider: e.target.value })}
          >
            <option value="Anthropic">Anthropic (Claude)</option>
            <option value="Google">Google (Gemini)</option>
            <option value="OpenAI">OpenAI (ChatGPT)</option>
          </select>
          <div className="faint">
            This decides which API and which account the model is called on. Getting it wrong makes
            the model fail rather than fall back to the other provider.
          </div>
        </div>

        <div className="field">
          <label htmlFor="modelName">Model name (must match what the API returns)</label>
          <input
            id="modelName"
            className="mono"
            value={form.modelName}
            onChange={(e) => patch({ modelName: e.target.value })}
            placeholder={
              { Google: 'gemini-3.8-flash', OpenAI: 'gpt-5.6-luna' }[form.provider] ?? 'claude-sonnet-5'
            }
            required
            maxLength={100}
          />
        </div>

        <div className="form-grid">
          <div className="field">
            <label htmlFor="inputPrice">Input (USD per 1M tokens)</label>
            <input
              id="inputPrice"
              type="number"
              step="0.0001"
              min="0"
              value={form.inputUsdPerMTok}
              onChange={(e) => syncCacheFromInput(e.target.value)}
              required
            />
            <div className="faint" style={{ fontSize: 12, marginTop: 4 }}>
              Changing this fills in the cache prices automatically.
            </div>
          </div>

          <div className="field">
            <label htmlFor="outputPrice">Output (USD per 1M tokens)</label>
            <input
              id="outputPrice"
              type="number"
              step="0.0001"
              min="0"
              value={form.outputUsdPerMTok}
              onChange={(e) => patch({ outputUsdPerMTok: e.target.value })}
              required
            />
          </div>

          <div className="field">
            <label htmlFor="cacheWrite">Cache write (USD per 1M)</label>
            <input
              id="cacheWrite"
              type="number"
              step="0.0001"
              min="0"
              value={form.cacheWriteUsdPerMTok}
              onChange={(e) => patch({ cacheWriteUsdPerMTok: e.target.value })}
              required
            />
          </div>

          <div className="field">
            <label htmlFor="cacheRead">Cache read (USD per 1M)</label>
            <input
              id="cacheRead"
              type="number"
              step="0.0001"
              min="0"
              value={form.cacheReadUsdPerMTok}
              onChange={(e) => patch({ cacheReadUsdPerMTok: e.target.value })}
              required
            />
          </div>

          <div className="field">
            <label htmlFor="rate">Exchange rate (THB per 1 USD)</label>
            <input
              id="rate"
              type="number"
              step="0.0001"
              min="0.0001"
              value={form.usdToThbRate}
              onChange={(e) => patch({ usdToThbRate: e.target.value })}
              required
            />
          </div>
        </div>

        <div className="field">
          <label htmlFor="notes">Notes</label>
          <input
            id="notes"
            value={form.notes}
            onChange={(e) => patch({ notes: e.target.value })}
            maxLength={500}
          />
        </div>

        <div className="field">
          <label className="checkbox">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => patch({ isActive: e.target.checked })}
            />
            Use this pricing
          </label>
        </div>

        <div className="alert alert-info" style={{ marginBottom: 14 }}>
          Example: a question using 700 input and 600 output tokens costs{' '}
          <strong>${sample.usd.toFixed(6)}</strong>, about{' '}
          <strong>{formatThb(sample.thb)} THB</strong> per call.
        </div>

        <div className="row-actions">
          <button type="submit" className="btn" disabled={busy}>
            {editingId ? 'Save changes' : 'Add pricing'}
          </button>
          {editingId && (
            <button type="button" className="btn btn-secondary" onClick={cancelEdit}>
              Cancel
            </button>
          )}
        </div>
      </form>
    </div>
  );
}
