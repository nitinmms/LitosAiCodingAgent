import { useState, type FormEvent } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { AttachmentSettings, SettingsSection } from '../api/types';
import { ErrorNote, SaveBar } from './bits';

const MB = 1024 * 1024;

/** The form as typed: the limits are text until saved, the total in megabytes. */
interface AttachmentsForm {
  enabled: boolean;
  maxFilesPerMessage: string;
  maxMegabytesPerThread: string;
}

export const toAttachmentsForm = (a: AttachmentSettings): AttachmentsForm => ({
  enabled: a.enabled,
  maxFilesPerMessage: String(a.maxFilesPerMessage),
  maxMegabytesPerThread: String(Math.round(a.maxBytesPerThread / MB)),
});

/** The settings the form describes, or why a number cannot be read; the host checks the ranges. */
export function fromAttachmentsForm(form: AttachmentsForm): { settings: AttachmentSettings } | { error: string } {
  const files = form.maxFilesPerMessage.trim();
  const megabytes = form.maxMegabytesPerThread.replace(/[\s,_]/g, '');
  if (!/^\d+$/.test(files)) return { error: 'Files per message must be a whole number.' };
  if (!/^\d+$/.test(megabytes)) return { error: 'The total per thread must be a whole number of megabytes.' };
  return { settings: { enabled: form.enabled, maxFilesPerMessage: Number(files), maxBytesPerThread: Number(megabytes) * MB } };
}

/**
 * The Attachments tab (attachments-architecture.md §1): whether people may attach files to a
 * task, and how many. The types are the Litos VS Code agent's and are not set here, nor is the
 * 20 MB a file every Litos face allows.
 */
export function AttachmentsTab({
  api,
  section,
  onSaved,
  onReload,
}: {
  api: FactoryApi;
  section: SettingsSection<AttachmentSettings>;
  onSaved: (saved: SettingsSection<AttachmentSettings>) => void;
  onReload: () => Promise<void>;
}) {
  const [form, setForm] = useState(() => toAttachmentsForm(section.settings));
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);

  const original = toAttachmentsForm(section.settings);
  const changed = JSON.stringify(form) !== JSON.stringify(original);
  const set = <K extends keyof AttachmentsForm>(key: K, value: AttachmentsForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const parsed = fromAttachmentsForm(form);
    if ('error' in parsed) {
      setError(parsed.error);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      onSaved(await api.saveAttachments(section.revision, parsed.settings));
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The attachment settings could not be saved.');
      setStale(failure instanceof ApiError && failure.status === 409);
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={submit} aria-label="Attachments" autoComplete="off">
      <section className="section">
        <h2>Files and images</h2>
        <p className="small muted">
          People can attach screenshots, documents and text files to a task, as in the Litos VS Code agent: images (PNG,
          JPEG, GIF, WebP) are shown to a model that reads them; PDF, Word, PowerPoint, Excel, EPUB and mail are converted to
          text; any other text file is read as it is. Other binary files are refused, and so is anything over 20 MB.
        </p>
        <label className="check">
          <input type="checkbox" checked={form.enabled} onChange={(e) => set('enabled', e.target.checked)} />
          <span>Anyone who can post to a task may attach files to it</span>
        </label>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="at-files">Files per message</label>
            <input
              id="at-files"
              inputMode="numeric"
              value={form.maxFilesPerMessage}
              disabled={!form.enabled}
              onChange={(e) => set('maxFilesPerMessage', e.target.value)}
            />
            <span className="hint">From 1 to 20.</span>
          </div>
          <div className="field">
            <label htmlFor="at-total">Total per task</label>
            <div className="with-unit">
              <input
                id="at-total"
                inputMode="numeric"
                value={form.maxMegabytesPerThread}
                disabled={!form.enabled}
                onChange={(e) => set('maxMegabytesPerThread', e.target.value)}
              />
              <span className="small muted">MB</span>
            </div>
            <span className="hint">All of one task&apos;s files together, from 20 to 1,024 MB.</span>
          </div>
        </div>
      </section>

      <ErrorNote message={error} />
      <p className="small muted">
        Turning attachments off stops new uploads; files already attached stay readable and still reach the task&apos;s runs.
      </p>
      <SaveBar changed={changed}>
        {stale ? (
          <button className="btn primary" type="button" onClick={() => void onReload()}>
            Load their change
          </button>
        ) : (
          <button className="btn primary" type="submit" disabled={busy || !changed}>
            Save attachments
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
      </SaveBar>
    </form>
  );
}
