import type { CurrentUser } from '../api/types';
import { initials } from '../domain/format';
import type { Route } from '../domain/route';
import { Mark } from './bits';

export function TopBar({
  user,
  view,
  awaitingYou,
  onNavigate,
  onAwaitingYou,
  onSignOut,
}: {
  user: CurrentUser;
  view: Route['view'];
  /** How many of the user's tasks are stopped until they act. */
  awaitingYou: number;
  onNavigate: (view: Exclude<Route['view'], 'invite'>) => void;
  onAwaitingYou: () => void;
  onSignOut: () => void;
}) {
  const name = user.displayName || user.userName;
  const tabs: [Exclude<Route['view'], 'invite'>, string][] = [
    ['threads', 'Threads'],
    ['projects', 'Projects'],
    // People, invitations and project membership are an admin's to manage.
    ...(user.roles.includes('Admin') ? [['people', 'People'] as [Exclude<Route['view'], 'invite'>, string]] : []),
  ];

  return (
    <header className="top">
      <div className="brand">
        <Mark /> Litos Factory
      </div>
      <nav className="nav" aria-label="Main">
        {tabs.map(([key, label]) => (
          <button key={key} aria-current={view === key ? 'page' : undefined} onClick={() => onNavigate(key)}>
            {label}
          </button>
        ))}
      </nav>
      <div className="top-right">
        <button className={`btn ${awaitingYou ? 'attn' : ''}`} onClick={onAwaitingYou} disabled={!awaitingYou}>
          {awaitingYou} awaiting you
        </button>
        <div className="userbox">
          <span className="avatar" aria-hidden="true">
            {initials(name)}
          </span>
          <span className="small">
            {name}
            {user.roles.includes('Admin') ? <span className="muted"> (Admin)</span> : null}
          </span>
          <button className="btn ghost small" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      </div>
    </header>
  );
}
