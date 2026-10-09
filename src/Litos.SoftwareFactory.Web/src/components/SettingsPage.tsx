import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { AdminSettings, BudgetSettings, SettingsSection } from '../api/types';
import { fmt } from '../domain/format';
import { navigate, SETTINGS_TABS, type SettingsTab } from '../domain/route';
import { ErrorNote } from './bits';

const TAB_LABELS: Record<SettingsTab, string> = { budgets: 'Budgets and limits' };

/**
 * The Admin's factory settings (blueprint §8.3, m3-architecture.md §3.4), one tab per section.
 * Each section is saved whole with the revision it was read at, so two Admins cannot overwrite
 * each other unseen; a change applies to what starts afterwards, with no restart.
 */
export function SettingsPage({
  api,
  tab,
  onNotice,
  onSaved,
}: {
  api: FactoryApi;
  tab: SettingsTab;
  onNotice: (text: string) => void;
  /** A section was saved: what members see of the settings may have changed. */
  onSaved: () => void;
}) {
  const [settings, setSettings] = useState<AdminSettings | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setError(null);
    try {
      setSettings(await api.adminSettings());
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The settings could not be loaded.');
    }
  }, [api]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return (
    <div className="page stack">
      <nav className="tabs" role="tablist" aria-label="Settings">
        {SETTINGS_TABS.map((t) => (
          <button key={t} role="tab" aria-selected={t === tab} onClick={() => navigate({ view: 'settings', tab: t })}>
            {TAB_LABELS[t]}
          </button>
        ))}
      </nav>
      <ErrorNote message={error} />
      {!settings ? (
        error ? null : (
          <p className="note">Loading…</p>
        )
      ) : (
        <BudgetsTab
          // A newer revision, saved here or reloaded after a conflict, starts the form again from it.
          key={settings.budgets.revision}
          api={api}
          section={settings.budgets}
          onSaved={(budgets) => {
            setSettings((current) => (current ? { ...current, budgets } : current));
            onNotice('The budgets are saved.');
            onSaved();
          }}
          onReload={reload}
        />
      )}
    </div>
  );
}

/** The form's fields, as typed: a token count may be written with separators, an empty one means none. */
interface BudgetForm {
  defaultTaskBudget: string;
  maximumTaskBudget: string;
  dailyUserQuota: string;
  monthlyUserQuota: string;
  outputAllowanceTokens: string;
  slotCap: string;
  repairCyclesPerRun: string;
  /** Percent of the first cap, as people think of it: 50, not 0.5. */
  reworkTopUpPercent: string;
}

const text = (value: number | null) => (value === null ? '' : String(value));

export const toForm = (b: BudgetSettings): BudgetForm => ({
  defaultTaskBudget: text(b.defaultTaskBudget),
  maximumTaskBudget: text(b.maximumTaskBudget),
  dailyUserQuota: text(b.dailyUserQuota),
  monthlyUserQuota: text(b.monthlyUserQuota),
  outputAllowanceTokens: text(b.outputAllowanceTokens),
  slotCap: text(b.slotCap),
  repairCyclesPerRun: text(b.repairCyclesPerRun),
  reworkTopUpPercent: text(Math.round(b.reworkTopUpShare * 1000) / 10),
});

/**
 * The settings the form describes, or why it cannot be read. Only the shape is checked here (whole
 * numbers where whole numbers go); the host checks the ranges and how the settings fit together,
 * and its reasons are shown as it gives them.
 */
export function fromForm(form: BudgetForm): { settings: BudgetSettings } | { error: string } {
  const problems: string[] = [];
  const whole = (value: string, name: string, optional: boolean): number | null => {
    const cleaned = value.replace(/[\s,_]/g, '');
    if (cleaned === '') {
      if (!optional) problems.push(`${name} is required.`);
      return null;
    }
    if (!/^\d+$/.test(cleaned)) {
      problems.push(`${name} must be a whole number.`);
      return null;
    }
    return Number(cleaned);
  };

  const topUp = form.reworkTopUpPercent.trim();
  const percent = topUp === '' ? NaN : Number(topUp);
  if (!Number.isFinite(percent) || percent < 0) problems.push('The change-request top-up must be a percentage, 0 or more.');

  const settings: BudgetSettings = {
    defaultTaskBudget: whole(form.defaultTaskBudget, 'The default task budget', true),
    maximumTaskBudget: whole(form.maximumTaskBudget, 'The maximum task budget', true),
    dailyUserQuota: whole(form.dailyUserQuota, 'The daily quota', true),
    monthlyUserQuota: whole(form.monthlyUserQuota, 'The monthly quota', true),
    outputAllowanceTokens: whole(form.outputAllowanceTokens, 'The output allowance', false) ?? 0,
    slotCap: whole(form.slotCap, 'Concurrent runs', false) ?? 0,
    repairCyclesPerRun: whole(form.repairCyclesPerRun, 'Repair cycles per run', false) ?? 0,
    reworkTopUpShare: percent / 100,
  };
  return problems.length ? { error: problems.join(' ') } : { settings };
}

function BudgetsTab({
  api,
  section,
  onSaved,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<BudgetSettings>;
  onSaved: (saved: SettingsSection<BudgetSettings>) => void;
  onReload: () => Promise<void>;
}) {
  const [form, setForm] = useState(() => toForm(section.settings));
  const [error, setError] = useState<string | null>(null);
  /** Someone else saved first: what is shown is out of date, and saving it would undo their change. */
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const original = toForm(section.settings);
  const changed = (Object.keys(form) as (keyof BudgetForm)[]).some((k) => form[k] !== original[k]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const parsed = fromForm(form);
    if ('error' in parsed) {
      setError(parsed.error);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveBudgets(section.revision, parsed.settings));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The budgets could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  const field = (key: keyof BudgetForm, label: string, hint: string, unit = 'tokens') => (
    <div className="field">
      <label htmlFor={`bs-${key}`}>{label}</label>
      <div className="with-unit">
        <input
          id={`bs-${key}`}
          inputMode="numeric"
          value={form[key]}
          onChange={(e) => setForm((f) => ({ ...f, [key]: e.target.value }))}
          aria-describedby={`bs-${key}-hint`}
        />
        <span className="small muted">{unit}</span>
      </div>
      <span className="hint" id={`bs-${key}-hint`}>
        {hint}
      </span>
    </div>
  );

  const shown = (value: string) => {
    const n = Number(value.replace(/[\s,_]/g, ''));
    return value.trim() && Number.isFinite(n) ? fmt(n) : null;
  };
  const daily = shown(form.dailyUserQuota);
  const monthly = shown(form.monthlyUserQuota);

  return (
    <form className="stack" onSubmit={submit} aria-label="Budgets and limits" autoComplete="off">
      <section className="section">
        <h2>Task budgets</h2>
        <div className="form-grid">
          {field('defaultTaskBudget', 'Default task budget', 'What a new thread gets when its creator gives none. Empty: no cap.')}
          {field('maximumTaskBudget', 'Maximum task budget', "No task's budget can be above this, when created or raised. Empty: no limit.")}
          {field('reworkTopUpPercent', 'Change-request top-up', "What each change request after a handoff adds, as a share of the task's first cap. 0 turns it off.", '%')}
        </div>
      </section>

      <section className="section">
        <h2>Quotas per person</h2>
        <div className="form-grid">
          {field('dailyUserQuota', 'Daily quota', "Tokens one person's tasks and chats may use in a day (UTC). Empty: none.")}
          {field('monthlyUserQuota', 'Monthly quota', "Tokens one person's tasks and chats may use in a calendar month (UTC). Empty: none.")}
        </div>
        <p className="small muted">
          {daily || monthly
            ? `A model call that would take someone past ${[daily && `${daily} today`, monthly && `${monthly} this month`].filter(Boolean).join(' or ')} is refused before it is sent, and their task pauses.`
            : 'Without a quota, only each task’s own budget limits what a person spends.'}
        </p>
      </section>

      <section className="section">
        <h2>Limits</h2>
        <div className="form-grid">
          {field('slotCap', 'Concurrent runs', 'Runs this host executes at once, from 1 to 16. Raising it may start a waiting task straight away.', 'runs')}
          {field('repairCyclesPerRun', 'Repair cycles per run', 'Times failing tests or a blocking finding go back to the agent before the run hands off or blocks, from 0 to 10.', 'cycles')}
          {field('outputAllowanceTokens', 'Output allowance per call', "What each model reply may use, a reasoning model's thinking included; it is reserved before the call is sent. From 4,096 to 200,000.")}
        </div>
      </section>

      <ErrorNote message={error} />
      <div className="btn-row">
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save budgets
          </button>
        )}
        <button
          className="btn"
          type="button"
          disabled={busy || !changed}
          onClick={() => {
            setForm(original);
            setError(null);
            setStale(false);
          }}
        >
          Discard changes
        </button>
      </div>
      <p className="small muted">
        Budgets and repair cycles apply to what starts afterwards: a running task keeps those it started with. Quotas and
        the output allowance apply from the next model call.
      </p>
    </form>
  );
}
