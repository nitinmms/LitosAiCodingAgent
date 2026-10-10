import { useMemo, useState } from 'react';
import type { CatalogModel } from '../api/types';
import { families, filterCatalog, modelFamily, priceText } from '../domain/catalog';
import { fmt } from '../domain/format';

/** Every row is this tall, so the rows in view follow from the scroll position alone. */
const ROW_HEIGHT = 36;
const VIEW_HEIGHT = 324;
/** Rows drawn beyond each edge of the view, so a fast scroll does not show blank space. */
const OVERSCAN = 6;

const MIN_CONTEXT_CHOICES = [0, 32_000, 128_000, 200_000, 1_000_000];

/**
 * The catalog picker (m3-architecture.md §4.3): an Admin searches one provider's catalog, narrows
 * it, ticks the models to allow, and adds them in one go, each with the context length the
 * provider reported. Catalogs run to hundreds of models, so only the rows in view are drawn.
 */
export function CatalogPicker({
  providerName,
  models,
  allowed,
  onAllow,
  onClose,
}: {
  providerName: string;
  models: CatalogModel[];
  /** Ids already allowed: hidden by default, and never added twice. */
  allowed: string[];
  onAllow: (models: { id: string; contextLength: number }[]) => void;
  onClose: () => void;
}) {
  const [search, setSearch] = useState('');
  const [toolsOnly, setToolsOnly] = useState(true);
  const [minContext, setMinContext] = useState(0);
  const [family, setFamily] = useState('');
  const [hideAllowed, setHideAllowed] = useState(true);
  const [ticked, setTicked] = useState<ReadonlySet<string>>(new Set());
  const [scrollTop, setScrollTop] = useState(0);

  const allowedSet = useMemo(() => new Set(allowed), [allowed]);
  const familyList = useMemo(() => families(models), [models]);
  const shown = useMemo(
    () => filterCatalog(models, { search, toolsOnly, minContext, family, hide: hideAllowed ? allowedSet : new Set() }),
    [models, search, toolsOnly, minContext, family, hideAllowed, allowedSet],
  );
  const reportsTools = models.some((m) => m.supportsTools !== null);

  const first = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN);
  const last = Math.min(shown.length, Math.ceil((scrollTop + VIEW_HEIGHT) / ROW_HEIGHT) + OVERSCAN);
  const id = (part: string) => `cp-${providerName}-${part}`;

  const toggle = (modelId: string) =>
    setTicked((current) => {
      const next = new Set(current);
      if (next.has(modelId)) next.delete(modelId);
      else next.add(modelId);
      return next;
    });

  const chosen = models.filter((m) => ticked.has(m.id) && !allowedSet.has(m.id) && m.contextLength !== null);

  return (
    <div className="catalog-picker stack" role="group" aria-label={`Choose ${providerName} models`}>
      <div className="catalog-filters">
        <div className="field">
          <label htmlFor={id('search')}>Search</label>
          <input id={id('search')} type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="claude sonnet" />
        </div>
        <div className="field">
          <label htmlFor={id('family')}>Family</label>
          <select id={id('family')} value={family} onChange={(e) => setFamily(e.target.value)}>
            <option value="">Every family</option>
            {familyList.map((f) => (
              <option key={f} value={f}>
                {f}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={id('context')}>Context at least</label>
          <select id={id('context')} value={minContext} onChange={(e) => setMinContext(Number(e.target.value))}>
            {MIN_CONTEXT_CHOICES.map((n) => (
              <option key={n} value={n}>
                {n === 0 ? 'Any' : `${fmt(n)} tokens`}
              </option>
            ))}
          </select>
        </div>
        <label className="check">
          <input type="checkbox" checked={toolsOnly} onChange={(e) => setToolsOnly(e.target.checked)} />
          <span>Takes tools</span>
        </label>
        <label className="check">
          <input type="checkbox" checked={hideAllowed} onChange={(e) => setHideAllowed(e.target.checked)} />
          <span>Hide allowed</span>
        </label>
      </div>
      {!reportsTools ? (
        <p className="small muted">This provider does not report which models take tools, so none are left out for it.</p>
      ) : null}

      <p className="small muted" aria-live="polite">
        {shown.length === models.length ? `${fmt(models.length)} models.` : `${fmt(shown.length)} of ${fmt(models.length)} models.`}
      </p>
      <div
        className="catalog-list"
        role="list"
        aria-label={`${providerName} catalog`}
        style={{ height: Math.min(VIEW_HEIGHT, Math.max(shown.length, 1) * ROW_HEIGHT) }}
        onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
      >
        <div style={{ height: shown.length * ROW_HEIGHT, position: 'relative' }}>
          {shown.slice(first, last).map((m, i) => {
            const isAllowed = allowedSet.has(m.id);
            const noContext = m.contextLength === null;
            return (
              <label
                key={m.id}
                role="listitem"
                className="catalog-row"
                style={{ top: (first + i) * ROW_HEIGHT, height: ROW_HEIGHT }}
                title={noContext ? 'Its context length is not reported: add it by its id below, with one you give.' : undefined}
              >
                <input
                  type="checkbox"
                  aria-label={m.id}
                  checked={isAllowed || ticked.has(m.id)}
                  disabled={isAllowed || noContext}
                  onChange={() => toggle(m.id)}
                />
                <span className="mono catalog-id">{m.id}</span>
                <span className="small muted catalog-name">{m.displayName && m.displayName !== m.id ? m.displayName : modelFamily(m.id)}</span>
                <span className="small">{noContext ? 'context not reported' : fmt(m.contextLength!)}</span>
                <span className="small muted">{m.supportsTools === null ? '' : m.supportsTools ? 'tools' : 'no tools'}</span>
                <span className="small muted">{priceText(m)}</span>
              </label>
            );
          })}
        </div>
      </div>

      <div className="btn-row">
        <button
          className="btn"
          type="button"
          disabled={chosen.length === 0}
          onClick={() => {
            onAllow(chosen.map((m) => ({ id: m.id, contextLength: m.contextLength! })));
            setTicked(new Set());
          }}
        >
          {chosen.length === 1 ? 'Allow 1 model' : `Allow ${chosen.length} models`}
        </button>
        <button className="btn ghost" type="button" onClick={onClose}>
          Close
        </button>
      </div>
      <p className="small muted">Prices are per million input and output tokens, as the provider lists them.</p>
    </div>
  );
}
