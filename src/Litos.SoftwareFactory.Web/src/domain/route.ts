import { useEffect, useState } from 'react';

export type Route =
  | { view: 'threads'; threadId: string | null }
  | { view: 'projects' }
  /** The Admin's people screen: accounts, invitations and project membership. */
  | { view: 'people' }
  /** An invitation's one-time link, opened by someone who is not signed in yet. */
  | { view: 'invite'; token: string };

/**
 * The app's routes live in the URL fragment (#/threads/<id>, #/projects, #/people,
 * #/invite/<token>), so the host only ever serves index.html and a reload lands on the same
 * screen. An invitation's token stays in the fragment, which the browser never sends to the host.
 */
export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts[0] === 'projects') return { view: 'projects' };
  if (parts[0] === 'people') return { view: 'people' };
  if (parts[0] === 'invite' && parts[1]) return { view: 'invite', token: decodeURIComponent(parts[1]) };
  return { view: 'threads', threadId: parts[0] === 'threads' && parts[1] ? decodeURIComponent(parts[1]) : null };
}

export const routeHash = (route: Route): string => {
  switch (route.view) {
    case 'projects':
      return '#/projects';
    case 'people':
      return '#/people';
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
