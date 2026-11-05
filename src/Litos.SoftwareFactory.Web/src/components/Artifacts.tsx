import type { ArtifactLink } from '../api/types';

/**
 * The pages a chat answer published (ReadMe_ArtifactsFeature.md §11.2): a card each, opening the
 * page in a new tab at the version this answer published. A later answer may have updated it;
 * then the card says so and links the newest version too.
 *
 * The page is served sandboxed by the host, so opening it in a tab is safe; `noopener` keeps the
 * new tab from reaching back into this one.
 */
export function MessageArtifacts({ artifacts }: { artifacts: ArtifactLink[] | undefined }) {
  if (!artifacts?.length) return null;
  return (
    <ul className="artifacts" aria-label="Published pages">
      {artifacts.map((a) => (
        <li key={`${a.id}-${a.version}`} className="artifact-card">
          <span className="artifact-icon" aria-hidden="true">
            ▦
          </span>
          <span className="artifact-text">
            <b className="artifact-title">{a.title}</b>
            <span className="small muted">
              {' '}
              version {a.version}
              {a.latestVersion > a.version ? (
                <>
                  {' · '}
                  <a href={a.latestUrl} target="_blank" rel="noopener noreferrer">
                    newer: version {a.latestVersion}
                  </a>
                </>
              ) : null}
            </span>
          </span>
          <a className="btn artifact-open" href={a.url} target="_blank" rel="noopener noreferrer" aria-label={`Open ${a.title} in a new tab`}>
            Open ↗
          </a>
        </li>
      ))}
    </ul>
  );
}
