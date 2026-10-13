import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { PresetSettings, SettingsSection, VerificationPreset } from '../api/types';
import { ErrorNote, SaveBar } from './bits';

/** A usable preset name, as Core/Settings/PresetSettings.cs checks it. */
export const isPresetName = (name: string) => /^[a-z0-9][a-z0-9-]{0,39}$/.test(name);

/** Why a profile is not JSON, as the browser reads it; the host checks the rest. */
export function jsonProblem(text: string): string | null {
  if (!text.trim()) return 'The profile is empty.';
  try {
    JSON.parse(text);
    return null;
  } catch (failure) {
    return `Not valid JSON: ${failure instanceof Error ? failure.message : String(failure)}`;
  }
}

/** One preset as the form holds it. */
interface PresetForm extends VerificationPreset {
  key: string;
}

let nextKey = 0;
const newKey = () => `preset-${++nextKey}`;

const toForm = (p: VerificationPreset): PresetForm => ({ ...p, key: newKey() });

/** The preset the form describes; the host checks it. */
export const fromPresetForm = (p: VerificationPreset): VerificationPreset => ({
  name: p.name.trim(),
  displayName: p.displayName.trim(),
  profile: p.profile,
});

const comparable = (presets: VerificationPreset[]) => JSON.stringify(presets.map(fromPresetForm));

/**
 * The Presets tab (blueprint §10.2, m3-architecture.md §8): the verification profiles a project
 * can be registered with, each written as JSON. A project copies its preset when registered, so
 * editing one here never changes a registered project; that is done on the Projects page.
 */
export function PresetsTab({
  api,
  section,
  onSaved,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<PresetSettings>;
  onSaved: (saved: SettingsSection<PresetSettings>) => void;
  onReload: () => Promise<void>;
}) {
  const [presets, setPresets] = useState(() => section.settings.presets.map(toForm));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const changed = comparable(presets) !== comparable(section.settings.presets);
  const update = (key: string, change: Partial<VerificationPreset>) => setPresets((all) => all.map((p) => (p.key === key ? { ...p, ...change } : p)));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onSaved(await api.savePresets(section.revision, { presets: presets.map(fromPresetForm) }));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The presets could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  // A new preset starts from the first one's profile, which is easier to adjust than to write.
  const add = () =>
    setPresets((all) => [...all, { key: newKey(), name: '', displayName: '', profile: all[0]?.profile ?? '' }]);

  return (
    <form className="stack" onSubmit={submit} aria-label="Presets" autoComplete="off">
      <section className="section">
        <h2>Verification presets</h2>
        <p className="small muted">
          A project is registered with one of these: the commands the host runs to restore, build and test it, and the reports
          it reads. The project keeps a copy, so editing a preset changes only projects registered afterwards. To change a
          registered project's profile, use the Projects page.
        </p>
        <div className="btn-row">
          <button className="btn" type="button" onClick={add}>
            Add a preset
          </button>
        </div>
      </section>

      {presets.map((preset, index) => (
        <PresetCard
          key={preset.key}
          preset={preset}
          first={index === 0}
          onChange={(change) => update(preset.key, change)}
          onRemove={() => setPresets((all) => all.filter((p) => p.key !== preset.key))}
        />
      ))}

      <ErrorNote message={error} />
      <SaveBar changed={changed}>
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save presets
          </button>
        )}
        <button
          className="btn"
          type="button"
          disabled={busy || !changed}
          onClick={() => {
            setPresets(section.settings.presets.map(toForm));
            setError(null);
            setStale(false);
          }}
        >
          Discard changes
        </button>
      </SaveBar>
    </form>
  );
}

function PresetCard({
  preset,
  first,
  onChange,
  onRemove,
}: {
  preset: PresetForm;
  first: boolean;
  onChange: (change: Partial<VerificationPreset>) => void;
  onRemove: () => void;
}) {
  const label = preset.displayName.trim() || preset.name.trim() || 'New preset';
  const id = (part: string) => `preset-${preset.key}-${part}`;
  const name = preset.name.trim();
  const problem = jsonProblem(preset.profile);
  return (
    <section className="section" aria-label={label}>
      <div className="section-head">
        <h2>{label}</h2>
        {first ? <span className="tag">Offered first</span> : null}
        <button className="btn ghost small" type="button" onClick={onRemove} aria-label={`Remove ${label}`}>
          Remove
        </button>
      </div>
      <div className="form-grid">
        <div className="field">
          <label htmlFor={id('name')}>Name</label>
          <input id={id('name')} className="mono" value={preset.name} onChange={(e) => onChange({ name: e.target.value })} placeholder="python" />
          <span className={name && !isPresetName(name) ? 'hint error' : 'hint'}>
            Lower-case letters, digits and '-', up to 40. Registration through the API names it.
          </span>
        </div>
        <div className="field">
          <label htmlFor={id('display')}>Shown as</label>
          <input id={id('display')} value={preset.displayName} onChange={(e) => onChange({ displayName: e.target.value })} placeholder="Python" />
        </div>
      </div>
      <div className="field">
        <label htmlFor={id('profile')}>Profile</label>
        <textarea
          id={id('profile')}
          className="mono"
          rows={14}
          spellCheck={false}
          value={preset.profile}
          onChange={(e) => onChange({ profile: e.target.value })}
          aria-describedby={id('profile-hint')}
        />
        <span className={problem ? 'hint error' : 'hint'} id={id('profile-hint')}>
          {problem ?? 'Steps, each with its restore, build and unit-test commands and the reports they write. The host checks it when you save.'}
        </span>
      </div>
    </section>
  );
}
