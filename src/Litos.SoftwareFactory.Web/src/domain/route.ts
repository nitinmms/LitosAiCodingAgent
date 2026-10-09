import { useEffect, useState } from 'react';

/** The tabs of the settings area; later M3 steps add providers, tools, MCP, skills and presets. */
export const SETTINGS_TABS = ['budgets'] as const;
export type SettingsTab = (typeof SETTINGS_TABS)[number];

export type Route =
  /** The board (§7.1): every visible task, in columns by stage. Where the app opens. */
  | { view: 'board' }
  | { view: 'threads'; threadId: string | null }
  | { view: 'projects' }
  /** The Admin's people screen: accounts, invitations and project membership. */
  | { view: 'people' }
  /** The Admin's factory settings, one tab per section. */
  | { view: 'settings'; tab: SettingsTab }
  /** An invitation's one-time link, opened by someone who is not signed in yet. */
  | { view: 'invite'; token: string };

/**
 * The app's routes live in the URL fragment (#/board, #/threads/<id>, #/projects, #/people,
 * #/settings/<tab>, #/invite/<token>), so the host only ever serves index.html and a reload lands
 * on the same screen. An invitation's token stays in the fragment, which the browser never sends
 * to the host.
 */
export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts.length === 0 || parts[0] === 'board') return { view: 'board' };
  if (parts[0] === 'projects') return { view: 'projects' };
  if (parts[0] === 'people') return { view: 'people' };
  if (parts[0] === 'settings')
    return { view: 'settings', tab: SETTINGS_TABS.find((t) => t === parts[1]) ?? SETTINGS_TABS[0] };
  if (parts[0] === 'invite' && parts[1]) return { view: 'invite', token: decodeURIComponent(parts[1]) };
  return { view: 'threads', threadId: parts[0] === 'threads' && parts[1] ? decodeURIComponent(parts[1]) : null };
}

export const routeHash = (route: Route): string => {
  switch (route.view) {
    case 'board':
      return '#/board';
    case 'projects':
      return '#/projects';
    case 'people':
      return '#/people';
    case 'settings':
      return `#/settings/${route.tab}`;
    case 'invite':
      return `#/invite/${encodeURIComponent(route.token)}`;
    default:
      return route.threadId ? `#/threads/${encodeURIComponent(route.threadId)}` : '#/threads';
  }
};

export function navigate(route: Route): void {
  const hash = routeHash(route);
  if (window.location.hash !== hash) window.location.hash = hash;
}

export function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.hash));
  useEffect(() => {
    const onChange = () => setRoute(parseRoute(window.location.hash));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return route;
}
