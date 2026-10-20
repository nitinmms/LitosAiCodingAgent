import { useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react';

/**
 * The Metrics tab's charts (m4-architecture.md §12): small SVG and HTML pieces drawn to one scale
 * each, coloured from the stylesheet's tokens so both themes work. Every mark carries a `data-tip`
 * the page's <TipLayer> shows on hover, and every chart has a text alternative: an aria-label, and
 * the table the page shows instead when "Tables" is chosen.
 */

/** A part of a whole: a labelled share in a colour token. */
export interface Segment {
  label: string;
  value: number;
  color: string;
}

/** The width an element is laid out at, kept current; `fallback` until it is measured (and in tests). */
export function useWidth<T extends HTMLElement>(fallback = 640): [RefObject<T | null>, number] {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => el.clientWidth > 0 && setWidth(el.clientWidth);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

/** A round step size for an axis from 0 to about `max`, and the axis' top. */
export function niceScale(max: number, ticks = 4): { max: number; step: number } {
  if (!(max > 0)) return { max: 1, step: 1 };
  const raw = max / ticks;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((k) => k * magnitude).find((s) => s >= raw)!;
  return { max: Math.ceil(max / step - 1e-9) * step, step };
}

export const tickValues = ({ max, step }: { max: number; step: number }): number[] =>
  Array.from({ length: Math.round(max / step) + 1 }, (_, i) => +(i * step).toFixed(6));

/**
 * The hover tooltip for every `data-tip` inside it: one floating box, plain text (a line per "\n"),
 * kept inside the window.
 */
export function TipLayer({ children }: { children: ReactNode }) {
  const [tip, setTip] = useState<{ text: string; x: number; y: number } | null>(null);
  return (
    <div
      onPointerMove={(e) => {
        const target = (e.target as Element).closest?.('[data-tip]');
        const text = target?.getAttribute('data-tip');
        setTip(text ? { text, x: e.clientX, y: e.clientY } : null);
      }}
      onPointerLeave={() => setTip(null)}
    >
      {children}
      {tip ? (
        <div
          className="chart-tip"
          role="tooltip"
          style={{ left: Math.min(tip.x + 14, window.innerWidth - 300), top: tip.y + 14 > window.innerHeight - 90 ? tip.y - 80 : tip.y + 14 }}
        >
          {tip.text}
        </div>
      ) : null}
    </div>
  );
}

/** A whole split into its parts, side by side, with a 2px gap; the label says what each part is. */
export function Meter({ segments, label, tall = false }: { segments: Segment[]; label: string; tall?: boolean }) {
  const total = segments.reduce((a, s) => a + s.value, 0);
  return (
    <div className={tall ? 'meter tall' : 'meter'} role="img" aria-label={`${label}: ${segments.map((s) => `${s.label} ${s.value}`).join(', ')}`}>
      {total === 0 ? <span className="meter-empty" /> : null}
      {segments
        .filter((s) => s.value > 0)
        .map((s) => (
          <span key={s.label} style={{ flex: s.value, background: s.color }} data-tip={`${s.label}: ${s.value} of ${total}`} />
        ))}
    </div>
  );
}

/** The key under a meter or chart: a swatch, a name and, when given, its figure. */
export function Key({ items }: { items: { label: string; color: string; value?: ReactNode }[] }) {
  return (
    <div className="chart-key">
      {items.map((i) => (
        <span key={i.label}>
          <i style={{ background: i.color }} aria-hidden="true" />
          {i.label}
          {i.value !== undefined ? <b> {i.value}</b> : null}
        </span>
      ))}
    </div>
  );
}

/** A small trend line with a faint area under it and its last point marked. A null point is a gap. */
export function Sparkline({ values, color, label, tip, width = 120, height = 36 }: {
  values: (number | null)[];
  color: string;
  label: string;
  tip: (value: number, index: number) => string;
  width?: number;
  height?: number;
}) {
  const known = values.filter((v): v is number => v !== null);
  if (values.length < 2 || known.length === 0) return null;
  const max = Math.max(1, ...known);
  const x = (i: number) => 3 + (i * (width - 6)) / (values.length - 1);
  const y = (v: number) => height - 3 - (v / max) * (height - 9);
  const points = values.map((v, i) => (v === null ? null : ([x(i), y(v)] as const))).filter((p) => p !== null);
  const line = points.map((p, i) => `${i ? 'L' : 'M'}${p[0].toFixed(1)} ${p[1].toFixed(1)}`).join(' ');
  const first = points[0]!;
  const last = points[points.length - 1]!;
  const slot = width / values.length;
  return (
    <svg className="spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label}>
      <path d={`${line} L${last[0]} ${height} L${first[0]} ${height}Z`} fill={color} opacity={0.12} />
      <path d={line} fill="none" stroke={color} strokeWidth={2} strokeLinejoin="round" />
      <circle cx={last[0]} cy={last[1]} r={3.5} fill={color} stroke="var(--surface)" strokeWidth={2} />
      {values.map((v, i) =>
        v === null ? null : <rect key={i} className="hit" x={x(i) - slot / 2} y={0} width={slot} height={height} data-tip={tip(v, i)} />,
      )}
    </svg>
  );
}

/** A value against a mark on one 0–1 scale: a filled bar and a tick where the mark is. */
export function Bullet({ value, mark, label, valueTip, markTip }: { value: number; mark: number; label: string; valueTip: string; markTip: string }) {
  return (
    <div className="bullet" role="img" aria-label={label}>
      <span className="bullet-fill" style={{ width: `${Math.min(1, value) * 100}%` }} data-tip={valueTip} />
      <span className="bullet-mark" style={{ left: `${Math.min(1, mark) * 100}%` }} data-tip={markTip} />
    </div>
  );
}

export interface RangeRow {
  label: string;
  count: number;
  median: number;
  p90: number;
  color: string;
  tip: string;
}

/**
 * Rows of spreads on one shared scale: a dot at the median, a tick at the 90th percentile, a band
 * between; the count at the right.
 */
export function RangeRows({ rows, unit, label, labelWidth = 150 }: { rows: RangeRow[]; unit: (v: number) => string; label: string; labelWidth?: number }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const left = Math.min(labelWidth, width * 0.38);
  const right = 48;
  const rowH = 30;
  const top = 4;
  const height = top + rows.length * rowH + 22;
  const scale = niceScale(Math.max(...rows.map((r) => r.p90), 0));
  const x = (v: number) => left + (v / scale.max) * (width - left - right);
  return (
    <div ref={ref}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label}>
        {tickValues(scale).map((t) => (
          <g key={t}>
            <line className="grid-line" x1={x(t)} x2={x(t)} y1={top} y2={height - 20} />
            <text x={x(t)} y={height - 5} textAnchor="middle">
              {unit(t)}
            </text>
          </g>
        ))}
        {rows.map((r, i) => {
          const y = top + i * rowH + rowH / 2;
          return (
            <g key={r.label} data-tip={r.tip}>
              <rect className="hit" x={0} y={y - rowH / 2} width={width} height={rowH} />
              <text className="chart-label" x={0} y={y + 4}>
                {r.label}
              </text>
              <line x1={x(r.median)} x2={x(r.p90)} y1={y} y2={y} stroke={r.color} strokeWidth={4} strokeLinecap="round" opacity={0.35} />
              <line x1={x(r.p90)} x2={x(r.p90)} y1={y - 6} y2={y + 6} stroke={r.color} strokeWidth={2} />
              <circle cx={x(r.median)} cy={y} r={5.5} fill={r.color} stroke="var(--surface)" strokeWidth={2} />
              <text className="chart-value" x={width - right + 10} y={y + 4}>
                {r.count}
              </text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}

export interface Column {
  label: string;
  /** Stacked from the baseline up, in order. */
  segments: Segment[];
  tip: string;
  /** Drawn as an outline: something counted apart from the rest (lessons left out). */
  outlined?: boolean;
}

/** Vertical columns on one scale, each a stack of parts, with its total above and its label below. */
export function Columns({ columns, label, caption }: { columns: Column[]; label: string; caption: string }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const height = 190;
  const top = 18;
  const bottom = 34;
  const gap = 12;
  const totals = columns.map((c) => c.segments.reduce((a, s) => a + s.value, 0));
  const max = Math.max(1, ...totals);
  const barW = Math.max(8, (width - gap * (columns.length - 1)) / columns.length);
  const scale = (v: number) => (v / max) * (height - top - bottom);
  return (
    <div ref={ref}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label}>
        <line className="axis-line" x1={0} x2={width} y1={height - bottom} y2={height - bottom} />
        {columns.map((c, i) => {
          const x = i * (barW + gap);
          let y = height - bottom;
          const marks = c.segments
            .filter((s) => s.value > 0)
            .map((s) => {
              const h = scale(s.value);
              y -= h;
              return c.outlined ? (
                <rect key={s.label} x={x + 1} y={y} width={barW - 2} height={h} rx={3} fill="var(--surface-2)" stroke={s.color} strokeDasharray="3 3" />
              ) : (
                <rect key={s.label} x={x} y={y + 1} width={barW} height={Math.max(0, h - 2)} rx={3} fill={s.color} />
              );
            });
          return (
            <g key={c.label} data-tip={c.tip}>
              <rect className="hit" x={x} y={0} width={barW} height={height} />
              {marks}
              <text className="chart-value" x={x + barW / 2} y={y - 6} textAnchor="middle">
                {totals[i]}
              </text>
              <text x={x + barW / 2} y={height - bottom + 16} textAnchor="middle">
                {c.label}
              </text>
            </g>
          );
        })}
        <text x={width / 2} y={height - 2} textAnchor="middle">
          {caption}
        </text>
      </svg>
    </div>
  );
}

export interface Dot {
  key: string;
  value: number;
  lane: number;
  color: string;
  /** Larger: needed rework. */
  big: boolean;
  /** Fainter: not finished. */
  faded: boolean;
  tip: string;
}

/**
 * Values as dots along one axis, one lane per group, with the median and 90th percentile drawn as
 * dashed lines. Dots are spread inside their lane by a fixed sequence, so a reload never moves them.
 */
export function DotStrip({ dots, lanes, median, p90, unit, label }: {
  dots: Dot[];
  lanes: string[];
  median: number;
  p90: number;
  unit: (v: number) => string;
  label: string;
}) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const left = lanes.length > 1 ? Math.min(160, width * 0.3) : 8;
  const laneH = 34;
  const top = 18;
  const height = top + lanes.length * laneH + 24;
  const scale = niceScale(Math.max(...dots.map((d) => d.value), 1));
  const x = (v: number) => left + (v / scale.max) * (width - left - 12);
  const spread = (i: number) => ((i * 0.618034) % 1) - 0.5;
  return (
    <div ref={ref}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label}>
        {tickValues(scale).map((t) => (
          <g key={t}>
            <line className="grid-line" x1={x(t)} x2={x(t)} y1={top} y2={height - 22} />
            <text x={x(t)} y={height - 6} textAnchor="middle">
              {unit(t)}
            </text>
          </g>
        ))}
        {lanes.length > 1
          ? lanes.map((name, i) => (
              <text key={name} className="chart-label" x={0} y={top + i * laneH + laneH / 2 + 4}>
                {name}
              </text>
            ))
          : null}
        {[
          [median, `median ${unit(median)}`, 'var(--ink)'],
          [p90, `p90 ${unit(p90)}`, 'var(--muted)'],
        ].map(([v, text, color]) => (
          <g key={text as string}>
            <line x1={x(v as number)} x2={x(v as number)} y1={4} y2={height - 22} stroke={color as string} strokeWidth={1.5} strokeDasharray="3 3" />
            <text className="chart-value" x={x(v as number) + 4} y={10}>
              {text}
            </text>
          </g>
        ))}
        {dots.map((d, i) => (
          <circle
            key={d.key}
            cx={x(d.value)}
            cy={top + d.lane * laneH + laneH / 2 + spread(i) * (laneH - 14)}
            r={d.big ? 6 : 4.5}
            fill={d.color}
            fillOpacity={d.faded ? 0.35 : 0.9}
            stroke="var(--surface)"
            strokeWidth={2}
            data-tip={d.tip}
          />
        ))}
      </svg>
    </div>
  );
}
