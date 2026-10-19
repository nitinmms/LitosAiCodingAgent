import { useCallback, useRef, useState } from 'react';
import { ApiError, type FactoryApi } from '../api/client';
import type { Attachment, AttachmentLimits } from '../api/types';

/** The image types every Litos face sends as images (attachments-architecture.md §1). */
const IMAGE_EXTENSIONS = ['.png', '.jpg', '.jpeg', '.gif', '.webp'];

export const isImageName = (name: string): boolean => IMAGE_EXTENSIONS.some((ext) => name.toLowerCase().endsWith(ext));

/** A size as people read it: 820 KB, 3.4 MB. */
export function fileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1).replace(/\.0$/, '')} MB`;
}

/** A file on its way into the next message: uploaded as soon as it is added. */
export interface PendingFile {
  key: string;
  name: string;
  size: number;
  status: 'uploading' | 'ready' | 'failed';
  attachment?: Attachment;
  error?: string;
}

let nextKey = 0;

/**
 * The composer's files: each uploads when it is added, so sending the message only names them.
 * A file refused here or by the host stays as a chip saying why, until it is removed.
 */
export function usePendingFiles(api: FactoryApi, threadId: string, limits: AttachmentLimits | undefined) {
  const [pending, setPending] = useState<PendingFile[]>([]);
  // Read by add(), which can run twice before React re-renders (a paste and a drop).
  const count = useRef(0);

  const update = (key: string, change: Partial<PendingFile>) =>
    setPending((files) => files.map((f) => (f.key === key ? { ...f, ...change } : f)));

  const add = useCallback(
    (files: File[]) => {
      if (!limits?.enabled || files.length === 0) return;
      for (const file of files) {
        const key = `f${++nextKey}`;
        const base = { key, name: file.name, size: file.size };
        // The file's own problem first: it would be refused whatever else is attached.
        if (file.size > limits.maxFileBytes) {
          setPending((list) => [...list, { ...base, status: 'failed', error: `Over the ${fileSize(limits.maxFileBytes)} limit.` }]);
          continue;
        }
        if (count.current >= limits.maxFilesPerMessage) {
          setPending((list) => [...list, { ...base, status: 'failed', error: `A message can carry at most ${limits.maxFilesPerMessage} files.` }]);
          continue;
        }

        count.current += 1;
        setPending((list) => [...list, { ...base, status: 'uploading' }]);
        api
          .uploadAttachment(threadId, file)
          .then((attachment) => update(key, { status: 'ready', attachment }))
          .catch((failure) => {
            count.current -= 1;
            update(key, { status: 'failed', error: failure instanceof ApiError ? failure.message : 'The upload failed.' });
          });
      }
    },
    [api, threadId, limits],
  );

  const remove = (key: string) =>
    setPending((files) => {
      const removed = files.find((f) => f.key === key);
      if (removed && removed.status !== 'failed') count.current -= 1;
      return files.filter((f) => f.key !== key);
    });

  const clear = () => {
    count.current = 0;
    setPending([]);
  };

  return {
    pending,
    add,
    remove,
    clear,
    uploading: pending.some((f) => f.status === 'uploading'),
    readyIds: pending.flatMap((f) => (f.status === 'ready' && f.attachment ? [f.attachment.id] : [])),
    hasImage: pending.some((f) => f.status !== 'failed' && isImageName(f.name)),
  };
}

/** The composer's files, each with how it stands and a button to take it off the message. */
export function PendingFiles({ files, onRemove }: { files: PendingFile[]; onRemove: (key: string) => void }) {
  if (files.length === 0) return null;
  return (
    <ul className="att-chips" aria-label="Files to send">
      {files.map((f) => (
        <li key={f.key} className={`att-chip ${f.status}`}>
          <span className="mono">{f.name}</span>
          <span className="small muted">
            {f.status === 'uploading' ? ' uploading…' : f.status === 'failed' ? ` ${f.error}` : ` ${fileSize(f.size)}`}
          </span>
          <button type="button" className="att-x" aria-label={`Remove ${f.name}`} onClick={() => onRemove(f.key)}>
            ×
          </button>
        </li>
      ))}
    </ul>
  );
}

/** A sent message's files: images as thumbnails, anything else as a file to save. */
export function MessageAttachments({ attachments }: { attachments: Attachment[] | undefined }) {
  if (!attachments?.length) return null;
  return (
    <div className="att-sent" aria-label="Attached files">
      {attachments.map((a) =>
        a.kind === 'Image' ? (
          <a key={a.id} href={a.url} target="_blank" rel="noopener noreferrer" title={a.fileName}>
            <img className="att-thumb" src={a.url} alt={a.fileName} loading="lazy" />
          </a>
        ) : (
          <a key={a.id} className="att-chip" href={a.url} download={a.fileName}>
            <span className="mono">{a.fileName}</span>
            <span className="small muted"> {fileSize(a.sizeBytes)}</span>
          </a>
        ),
      )}
    </div>
  );
}
