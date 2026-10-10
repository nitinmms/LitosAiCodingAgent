import { useState } from 'react';
import type { OfferedProvider } from '../api/types';
import type { ModelChoice } from '../domain/catalog';
import { fmt } from '../domain/format';

/**
 * New thread's model choice (m3-architecture.md §4.4): one searchable list, grouped by provider,
 * with the factory's default and the person's recent models pinned above. A provider that allows
 * every model can offer hundreds, so the search matches the model id and the provider's name.
 */
export function ModelChooser({
  offered,
  pinned,
  value,
  onChange,
}: {
  offered: OfferedProvider[];
  pinned: ModelChoice[];
  value: { provider: string; model: string } | null;
  onChange: (choice: { provider: string; model: string }) => void;
}) {
  const [search, setSearch] = useState('');
  const words = search.toLowerCase().split(/\s+/).filter(Boolean);
  const matches = (provider: OfferedProvider, model: string) =>
    words.every((w) => model.toLowerCase().includes(w) || provider.displayName.toLowerCase().includes(w));

  const groups = offered
    .map((p) => ({ provider: p, models: p.models.filter((m) => matches(p, m.id)) }))
    .filter((g) => g.models.length > 0);
  const selectedProvider = offered.find((p) => p.name === value?.provider) ?? null;
  const selected = selectedProvider?.models.find((m) => m.id === value?.model) ?? null;

  const option = (provider: OfferedProvider, model: string, contextLength: number, key: string) => {
    const isSelected = provider.name === value?.provider && model === value.model;
    return (
      <button
        key={key}
        type="button"
        role="option"
        aria-selected={isSelected}
        aria-label={`${model} on ${provider.displayName}`}
        className="model-option"
        onClick={() => onChange({ provider: provider.name, model })}
      >
        <span className="mono">{model}</span>
        <span className="small muted">{fmt(contextLength)}</span>
      </button>
    );
  };

  return (
    <div className="field model-chooser">
      <label htmlFor="nt-model-search">Model</label>
      <input
        id="nt-model-search"
        type="search"
        placeholder="Search models"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        // Enter picks the only match, rather than creating the thread.
        onKeyDown={(e) => {
          if (e.key !== 'Enter') return;
          e.preventDefault();
          const only = groups.length === 1 && groups[0]!.models.length === 1 ? groups[0]! : null;
          if (only) onChange({ provider: only.provider.name, model: only.models[0]!.id });
        }}
      />
      <div className="model-list" role="listbox" aria-label="Models">
        {!words.length && pinned.length ? (
          <div role="group" aria-label="Default and recent">
            <div className="model-group" aria-hidden="true">
              Default and recent
            </div>
            {pinned.map((c) => option(c.provider, c.id, c.contextLength, `pinned-${c.provider.name}-${c.id}`))}
          </div>
        ) : null}
        {groups.map((g) => (
          <div key={g.provider.name} role="group" aria-label={g.provider.displayName}>
            <div className="model-group" aria-hidden="true">
              {g.provider.displayName}
              {g.provider.budgetPrecision === 'estimated' ? ' (estimated budget)' : ''}
            </div>
            {g.models.map((m) => option(g.provider, m.id, m.contextLength, `${g.provider.name}-${m.id}`))}
          </div>
        ))}
        {groups.length === 0 ? <p className="small muted model-group">No model matches.</p> : null}
      </div>
      <span className="hint">
        {selected && selectedProvider ? `${selectedProvider.displayName}, ${selected.id}. Context ${fmt(selected.contextLength)} tokens.` : 'Choose a model.'}
      </span>
    </div>
  );
}
