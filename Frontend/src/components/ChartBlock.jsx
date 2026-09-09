import { useMemo, useState } from 'react';

/**
 * Renders a chart from a JSON spec the model emitted inside a ```chart fenced block.
 *
 * The model never sends code, only data — the alternative (letting it write plotting code
 * we execute) would mean running untrusted code in the browser. A spec also means the chart
 * lives inside the stored message, so re-opening an old conversation re-renders it, and an
 * auditor reading the log sees the same numbers the employee saw.
 *
 * Anything malformed falls back to showing the raw block as code rather than throwing: a bad
 * chart must never take the whole answer down with it.
 */

/**
 * Categorical palette, fixed slot order — assigned by series index, never cycled.
 * The order is the colour-blind-safety mechanism, not decoration: this sequence clears the
 * adjacent-pair CVD and normal-vision gates (worst adjacent ΔE 9.1 protan / 19.6 normal).
 *
 * Three of these sit below 3:1 against a white surface, which obliges relief — that is why the
 * legend is always present for two or more series and the data table is one click away.
 * (The app is light-theme only; a dark theme would need its own steps, not an automatic flip.)
 */
const SERIES_COLORS = [
  '#2a78d6', // blue
  '#eb6834', // orange
  '#1baf7a', // aqua
  '#eda100', // yellow
  '#e87ba4', // magenta
  '#008300', // green
];

const MAX_SERIES = SERIES_COLORS.length;
const MAX_LABELS = 40;
const MAX_PIE_SLICES = 6;

/** Surface colour used for the 2px gaps and rings that separate touching marks. */
const SURFACE = '#ffffff';

const KNOWN_TYPES = ['bar', 'hbar', 'line', 'pie'];

// ---------------------------------------------------------------- parsing

/**
 * Validates the model's spec. Returns { ok: true, spec } or { ok: false, reason }.
 * Strict on purpose — a half-valid chart that renders wrong numbers is worse than a code block.
 */
export function parseChartSpec(raw) {
  let data;
  try {
    data = JSON.parse(raw);
  } catch {
    return { ok: false, reason: 'the block is not valid JSON' };
  }

  if (!data || typeof data !== 'object' || Array.isArray(data)) {
    return { ok: false, reason: 'the spec must be a JSON object' };
  }

  const type = String(data.type ?? '').toLowerCase();
  if (!KNOWN_TYPES.includes(type)) {
    return { ok: false, reason: `unknown chart type "${data.type}"` };
  }

  const labels = Array.isArray(data.labels)
    ? data.labels.map((l) => String(l ?? '').trim()).filter((l) => l.length > 0)
    : [];
  if (labels.length === 0) return { ok: false, reason: 'labels is empty' };
  if (labels.length > MAX_LABELS) {
    return { ok: false, reason: `${labels.length} labels is more than a readable chart holds` };
  }

  const rawSeries = Array.isArray(data.series) ? data.series : [];
  if (rawSeries.length === 0) return { ok: false, reason: 'series is empty' };

  const series = [];
  for (const s of rawSeries.slice(0, MAX_SERIES)) {
    const values = Array.isArray(s?.values) ? s.values : null;
    if (!values) return { ok: false, reason: 'a series has no values array' };

    // null, undefined and '' must be rejected before any coercion: Number(null) is 0, so a
    // missing cell would otherwise be drawn as a real zero and misstate the data.
    if (values.some((v) => v === null || v === undefined || v === '')) {
      return { ok: false, reason: 'a series has a missing value — a gap must not be charted as zero' };
    }

    const numbers = values.map((v) => (typeof v === 'number' ? v : Number(v)));
    if (numbers.some((n) => !Number.isFinite(n))) {
      return { ok: false, reason: 'a series contains a value that is not a number' };
    }
    if (numbers.length !== labels.length) {
      return {
        ok: false,
        reason: `a series has ${numbers.length} values but there are ${labels.length} labels`,
      };
    }

    series.push({ name: String(s?.name ?? '').trim(), values: numbers });
  }

  return {
    ok: true,
    spec: {
      type,
      title: String(data.title ?? '').trim(),
      unit: String(data.unit ?? '').trim(),
      labels,
      series,
      // More series than the palette holds are dropped rather than given invented hues.
      droppedSeries: Math.max(0, rawSeries.length - MAX_SERIES),
    },
  };
}

// ---------------------------------------------------------------- formatting

const fullNumber = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 2 });

/** Axis ticks stay short; 12,000 becomes 12K so the left margin does not eat the plot. */
function compact(value) {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return `${trimZero(value / 1_000_000)}M`;
  if (abs >= 10_000) return `${trimZero(value / 1000)}K`;
  return fullNumber.format(value);
}

const trimZero = (n) => Number(n.toFixed(1)).toString();

const withUnit = (value, unit) => (unit ? `${fullNumber.format(value)} ${unit}` : fullNumber.format(value));

/**
 * Axis bounds on clean numbers (0 / 1,000 / 2,000), because ticks carry every value that is
 * not directly labelled.
 */
function niceScale(min, max, tickTarget = 5) {
  const lo = Math.min(0, min);
  const hi = Math.max(0, max);

  if (lo === hi) return { lo: 0, hi: hi === 0 ? 1 : hi * 1.2, ticks: [0, hi === 0 ? 1 : hi * 1.2] };

  const rawStep = (hi - lo) / tickTarget;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= rawStep) ?? magnitude * 10;

  const niceLo = Math.floor(lo / step) * step;
  const niceHi = Math.ceil(hi / step) * step;

  const ticks = [];
  for (let t = niceLo; t <= niceHi + step / 1000; t += step) {
    ticks.push(Number(t.toFixed(10)));
  }

  return { lo: niceLo, hi: niceHi, ticks };
}

/** A bar with its data-end rounded and its baseline end square. */
function barPath(x, y, w, h, r = 4, horizontal = false) {
  if (h <= 0 || w <= 0) return '';
  const radius = Math.min(r, horizontal ? w : h, (horizontal ? h : w) / 2);

  if (horizontal) {
    return `M${x},${y} H${x + w - radius} A${radius},${radius} 0 0 1 ${x + w},${y + radius}` +
           ` V${y + h - radius} A${radius},${radius} 0 0 1 ${x + w - radius},${y + h} H${x} Z`;
  }
  return `M${x},${y + h} V${y + radius} A${radius},${radius} 0 0 1 ${x + radius},${y}` +
         ` H${x + w - radius} A${radius},${radius} 0 0 1 ${x + w},${y + radius} V${y + h} Z`;
}

// ---------------------------------------------------------------- shared chrome

/** In-SVG tooltip: drawn in chart coordinates so it cannot be mispositioned by a scrolling bubble. */
function Tooltip({ x, y, lines, width }) {
  const w = Math.max(...lines.map((l) => l.length)) * 6.4 + 16;
  const h = lines.length * 15 + 10;
  // Flip to the left near the right edge so the box never leaves the plot.
  const left = x + w + 8 > width ? x - w - 8 : x + 8;

  return (
    <g className="viz-tip" pointerEvents="none">
      <rect x={left} y={y - h / 2} width={w} height={h} rx="4" />
      {lines.map((line, i) => (
        <text key={i} x={left + 8} y={y - h / 2 + 17 + i * 15}>{line}</text>
      ))}
    </g>
  );
}

function Legend({ series }) {
  // One series needs no legend — the title already says what is plotted, and a lone swatch
  // just restates it.
  if (series.length < 2) return null;

  return (
    <div className="viz-legend">
      {series.map((s, i) => (
        <span key={i}>
          <i style={{ background: SERIES_COLORS[i] }} />
          {s.name || `Series ${i + 1}`}
        </span>
      ))}
    </div>
  );
}

/**
 * The table view. Always available, not a nicety: three palette hues fall below 3:1 against
 * white, and the relief for that is visible labels or a table.
 */
function DataTable({ spec }) {
  return (
    <div className="viz-table-wrap">
      <table className="viz-table">
        <thead>
          <tr>
            <th />
            {spec.series.map((s, i) => (
              <th key={i} className="num">
                <i style={{ background: SERIES_COLORS[i] }} />
                {s.name || `Series ${i + 1}`}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {spec.labels.map((label, r) => (
            <tr key={r}>
              <th scope="row">{label}</th>
              {spec.series.map((s, i) => (
                <td key={i} className="num">{fullNumber.format(s.values[r])}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---------------------------------------------------------------- bar / column

function BarChart({ spec, horizontal }) {
  const [hover, setHover] = useState(null);

  const W = 720;
  const padTop = 16;
  const padBottom = horizontal ? 34 : 46;
  const padLeft = horizontal ? Math.min(180, 14 + Math.max(...spec.labels.map((l) => l.length)) * 7) : 56;
  const padRight = 16;

  const bandCount = spec.labels.length;
  const plotH = horizontal ? Math.max(120, bandCount * spec.series.length * 22 + bandCount * 12) : 260;
  const H = plotH + padTop + padBottom;
  const plotW = W - padLeft - padRight;

  const all = spec.series.flatMap((s) => s.values);
  const { lo, hi, ticks } = niceScale(Math.min(...all), Math.max(...all));
  const span = hi - lo || 1;

  const value = (v) => (v - lo) / span;
  const band = (horizontal ? plotH : plotW) / bandCount;
  // Cap thickness and leave the band's remainder as air; 2px of that is the gap between neighbours.
  const thickness = Math.min(24, Math.max(4, (band - 12) / spec.series.length - 2));
  const groupSize = thickness * spec.series.length + 2 * (spec.series.length - 1);

  // The extreme is the one value worth a direct label; the axis carries the rest.
  const peak = Math.max(...all);

  return (
    <>
      <svg viewBox={`0 0 ${W} ${H}`} className="viz-svg" role="img"
        aria-label={spec.title || 'Chart'} onMouseLeave={() => setHover(null)}>
        {/* gridlines: solid hairlines one step off the surface, never dashed */}
        {ticks.map((t, i) => {
          const p = value(t);
          return horizontal ? (
            <line key={i} className="viz-grid" x1={padLeft + p * plotW} y1={padTop}
              x2={padLeft + p * plotW} y2={padTop + plotH} />
          ) : (
            <line key={i} className="viz-grid" x1={padLeft} y1={padTop + (1 - p) * plotH}
              x2={padLeft + plotW} y2={padTop + (1 - p) * plotH} />
          );
        })}

        {/* axis ticks */}
        {ticks.map((t, i) => {
          const p = value(t);
          return horizontal ? (
            <text key={i} className="viz-tick" x={padLeft + p * plotW} y={padTop + plotH + 16}
              textAnchor="middle">{compact(t)}</text>
          ) : (
            <text key={i} className="viz-tick" x={padLeft - 8} y={padTop + (1 - p) * plotH + 4}
              textAnchor="end">{compact(t)}</text>
          );
        })}

        {/* category labels */}
        {spec.labels.map((label, i) => {
          const centre = i * band + band / 2;
          return horizontal ? (
            <text key={i} className="viz-cat" x={padLeft - 10} y={padTop + centre + 4} textAnchor="end">
              {label.length > 26 ? `${label.slice(0, 25)}…` : label}
            </text>
          ) : (
            <text key={i} className="viz-cat" x={padLeft + centre} y={padTop + plotH + 18}
              textAnchor="middle" transform={bandCount > 8
                ? `rotate(-35 ${padLeft + centre} ${padTop + plotH + 18})` : undefined}>
              {label.length > 14 ? `${label.slice(0, 13)}…` : label}
            </text>
          );
        })}

        {/* bars */}
        {spec.series.map((s, si) =>
          s.values.map((v, li) => {
            const centre = li * band + band / 2;
            const offset = centre - groupSize / 2 + si * (thickness + 2);
            const zero = value(0);
            const p = value(v);
            const active = hover?.li === li && hover?.si === si;

            const d = horizontal
              ? barPath(padLeft + Math.min(zero, p) * plotW, padTop + offset,
                  Math.abs(p - zero) * plotW, thickness, 4, true)
              : barPath(padLeft + offset, padTop + (1 - Math.max(zero, p)) * plotH,
                  thickness, Math.abs(p - zero) * plotH, 4, false);

            return (
              <path key={`${si}-${li}`} d={d} fill={SERIES_COLORS[si]}
                opacity={hover && !active ? 0.45 : 1}
                onMouseEnter={() => setHover({ si, li })}>
                <title>{`${spec.labels[li]} — ${s.name || 'value'}: ${withUnit(v, spec.unit)}`}</title>
              </path>
            );
          }))}

        {/* one direct label on the extreme */}
        {spec.series.map((s, si) =>
          s.values.map((v, li) => {
            if (v !== peak) return null;
            const centre = li * band + band / 2;
            const offset = centre - groupSize / 2 + si * (thickness + 2) + thickness / 2;
            const p = value(v);
            return horizontal ? (
              <text key={`p${si}-${li}`} className="viz-value" x={padLeft + p * plotW + 6}
                y={padTop + offset + 4}>{compact(v)}</text>
            ) : (
              <text key={`p${si}-${li}`} className="viz-value" x={padLeft + offset}
                y={padTop + (1 - p) * plotH - 6} textAnchor="middle">{compact(v)}</text>
            );
          }))}

        {hover && (() => {
          const s = spec.series[hover.si];
          const centre = hover.li * band + band / 2;
          const p = value(s.values[hover.li]);
          const tx = horizontal ? padLeft + p * plotW : padLeft + centre;
          const ty = horizontal ? padTop + centre : padTop + (1 - p) * plotH;
          return <Tooltip x={tx} y={Math.max(24, ty)} width={W}
            lines={[spec.labels[hover.li],
              `${s.name ? s.name + ': ' : ''}${withUnit(s.values[hover.li], spec.unit)}`]} />;
        })()}
      </svg>
    </>
  );
}

// ---------------------------------------------------------------- line

function LineChart({ spec }) {
  const [hover, setHover] = useState(null);

  const W = 720, H = 300, padTop = 16, padBottom = 46, padLeft = 56, padRight = 24;
  const plotW = W - padLeft - padRight;
  const plotH = H - padTop - padBottom;

  const all = spec.series.flatMap((s) => s.values);
  const { lo, hi, ticks } = niceScale(Math.min(...all), Math.max(...all));
  const span = hi - lo || 1;

  const px = (i) => padLeft + (spec.labels.length === 1
    ? plotW / 2
    : (i / (spec.labels.length - 1)) * plotW);
  const py = (v) => padTop + (1 - (v - lo) / span) * plotH;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="viz-svg" role="img"
      aria-label={spec.title || 'Chart'} onMouseLeave={() => setHover(null)}>
      {ticks.map((t, i) => (
        <line key={i} className="viz-grid" x1={padLeft} y1={py(t)} x2={padLeft + plotW} y2={py(t)} />
      ))}
      {ticks.map((t, i) => (
        <text key={i} className="viz-tick" x={padLeft - 8} y={py(t) + 4} textAnchor="end">{compact(t)}</text>
      ))}

      {spec.labels.map((label, i) => {
        // Thin out x labels rather than let them collide.
        const every = Math.ceil(spec.labels.length / 12);
        if (i % every !== 0 && i !== spec.labels.length - 1) return null;
        return (
          <text key={i} className="viz-cat" x={px(i)} y={padTop + plotH + 18} textAnchor="middle">
            {label.length > 12 ? `${label.slice(0, 11)}…` : label}
          </text>
        );
      })}

      {/* hover band: a full-height target per x, far bigger than the marker itself */}
      {spec.labels.map((_, i) => (
        <rect key={i} x={px(i) - plotW / (spec.labels.length * 2 || 1)} y={padTop}
          width={plotW / (spec.labels.length || 1)} height={plotH} fill="transparent"
          onMouseEnter={() => setHover(i)} />
      ))}

      {hover !== null && (
        <line className="viz-crosshair" x1={px(hover)} y1={padTop} x2={px(hover)} y2={padTop + plotH} />
      )}

      {spec.series.map((s, si) => (
        <polyline key={si} className="viz-line" stroke={SERIES_COLORS[si]}
          points={s.values.map((v, i) => `${px(i)},${py(v)}`).join(' ')} />
      ))}

      {/* markers carry a 2px surface ring so they stay legible where lines cross */}
      {spec.series.map((s, si) =>
        s.values.map((v, i) => (
          (hover === i || spec.labels.length <= 12) ? (
            <circle key={`${si}-${i}`} cx={px(i)} cy={py(v)} r="4.5" fill={SERIES_COLORS[si]}
              stroke={SURFACE} strokeWidth="2">
              <title>{`${spec.labels[i]} — ${s.name || 'value'}: ${withUnit(v, spec.unit)}`}</title>
            </circle>
          ) : null
        )))}

      {/* endpoint labels only while few enough series that they cannot collide */}
      {spec.series.length <= 2 && spec.series.map((s, si) => {
        const last = s.values.length - 1;
        return (
          <text key={si} className="viz-value" x={px(last) + 6} y={py(s.values[last]) + 4}>
            {compact(s.values[last])}
          </text>
        );
      })}

      {hover !== null && (
        <Tooltip x={px(hover)} y={Math.max(28, py(spec.series[0].values[hover]))} width={W}
          lines={[spec.labels[hover],
            ...spec.series.map((s) => `${s.name ? s.name + ': ' : ''}${withUnit(s.values[hover], spec.unit)}`)]} />
      )}
    </svg>
  );
}

// ---------------------------------------------------------------- pie

function PieChart({ spec }) {
  const [hover, setHover] = useState(null);

  const W = 720, H = 280;
  const cx = 170, cy = H / 2, r = 105;

  const values = spec.series[0].values.map((v) => Math.abs(v));
  const total = values.reduce((a, b) => a + b, 0) || 1;

  let angle = -Math.PI / 2;
  const slices = values.map((v, i) => {
    const sweep = (v / total) * Math.PI * 2;
    const start = angle;
    angle += sweep;
    return { i, v, start, end: angle, share: v / total };
  });

  const point = (a, radius) => [cx + Math.cos(a) * radius, cy + Math.sin(a) * radius];

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="viz-svg" role="img"
      aria-label={spec.title || 'Chart'} onMouseLeave={() => setHover(null)}>
      {slices.map((s) => {
        const [x1, y1] = point(s.start, r);
        const [x2, y2] = point(s.end, r);
        const large = s.end - s.start > Math.PI ? 1 : 0;
        const active = hover === s.i;

        return (
          <path key={s.i}
            d={`M${cx},${cy} L${x1},${y1} A${r},${r} 0 ${large} 1 ${x2},${y2} Z`}
            fill={SERIES_COLORS[s.i]}
            // A 2px stroke in the surface colour is the gap that separates touching slices —
            // the same mechanism as the gap between bars, not a border.
            stroke={SURFACE} strokeWidth="2"
            opacity={hover !== null && !active ? 0.45 : 1}
            onMouseEnter={() => setHover(s.i)}>
            <title>{`${spec.labels[s.i]}: ${withUnit(s.v, spec.unit)} (${(s.share * 100).toFixed(1)}%)`}</title>
          </path>
        );
      })}

      {/* the labels double as the relief for the low-contrast hues */}
      {slices.map((s, idx) => (
        <g key={s.i}>
          <rect className="viz-key" x={330} y={38 + idx * 30} width="12" height="12"
            fill={SERIES_COLORS[s.i]} rx="2" />
          <text className="viz-cat" x={350} y={49 + idx * 30}>
            {spec.labels[s.i].length > 24 ? `${spec.labels[s.i].slice(0, 23)}…` : spec.labels[s.i]}
          </text>
          <text className="viz-value" x={W - 24} y={49 + idx * 30} textAnchor="end">
            {fullNumber.format(s.v)} · {(s.share * 100).toFixed(1)}%
          </text>
        </g>
      ))}
    </svg>
  );
}

// ---------------------------------------------------------------- stat tile

/** One number is not a chart. A single bar becomes the figure it always was. */
function StatTile({ spec }) {
  const v = spec.series[0].values[0];
  return (
    <div className="viz-stat">
      <span className="viz-stat-label">{spec.labels[0]}</span>
      <strong className="viz-stat-value">{withUnit(v, spec.unit)}</strong>
    </div>
  );
}

// ---------------------------------------------------------------- entry point

export default function ChartBlock({ raw }) {
  const parsed = useMemo(() => parseChartSpec(raw), [raw]);
  const [showTable, setShowTable] = useState(false);

  if (!parsed.ok) {
    // Fall back to the raw block so the answer survives, and say why it did not draw.
    return (
      <div className="code-block">
        <div className="code-head">
          <span className="code-lang">chart</span>
          <span className="code-lines">could not be drawn — {parsed.reason}</span>
        </div>
        <pre><code>{raw}</code></pre>
      </div>
    );
  }

  const { spec } = parsed;
  const notes = [];

  let type = spec.type;
  let single = { ...spec };

  if (type === 'pie') {
    // Part-to-whole reads at a glance only: under three slices there is no whole to see, and
    // past six the slices stop being distinguishable. A bar answers both cases.
    if (spec.labels.length < 3 || spec.labels.length > MAX_PIE_SLICES) {
      type = 'hbar';
      notes.push(spec.labels.length < MAX_PIE_SLICES
        ? 'shown as bars — a pie of fewer than three slices has no whole to read'
        : `shown as bars — ${spec.labels.length} slices is past what a pie can separate`);
    } else {
      // A pie plots one series; extra ones are meaningless here.
      single = { ...spec, series: [spec.series[0]] };
    }
  }

  if ((type === 'bar' || type === 'hbar') && spec.labels.length === 1 && spec.series.length === 1) {
    return (
      <figure className="viz-figure">
        {spec.title && <figcaption className="viz-title">{spec.title}</figcaption>}
        <StatTile spec={spec} />
        <div className="viz-note">shown as a figure — a one-bar chart is just its number</div>
      </figure>
    );
  }

  if (spec.droppedSeries > 0) {
    notes.push(`${spec.droppedSeries} further series not shown — ${MAX_SERIES} is the readable limit`);
  }

  return (
    <figure className="viz-figure">
      {spec.title && <figcaption className="viz-title">{spec.title}</figcaption>}

      {type === 'line' && <LineChart spec={single} />}
      {type === 'bar' && <BarChart spec={single} horizontal={false} />}
      {type === 'hbar' && <BarChart spec={single} horizontal />}
      {type === 'pie' && <PieChart spec={single} />}

      {type !== 'pie' && <Legend series={single.series} />}

      <div className="viz-foot">
        <button type="button" className="btn-link" onClick={() => setShowTable((v) => !v)}>
          {showTable ? 'Hide data table' : 'Show data table'}
        </button>
        {spec.unit && <span className="viz-unit">unit: {spec.unit}</span>}
      </div>

      {notes.map((n, i) => <div key={i} className="viz-note">{n}</div>)}

      {showTable && <DataTable spec={single} />}
    </figure>
  );
}
