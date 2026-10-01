import { useEffect, useState } from 'react';

export type Route = { view: 'threads'; threadId: string | null } | { view: 'projects' };

/**
 * The app's routes live in the URL fragment (#/threads/<id>, #/projects), so the host only
 * ever serves index.html and a reload lands on the same screen.
 */
export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts[0] === 'projects') return { view: 'projects' };
  return { view: 'threads', threadId: parts[0] === 'threads' && parts[1] ? decodeURIComponent(parts[1]) : null };
}

export const routeHash = (route: Route): string =>
  route.view === 'projects' ? '#/projects' : route.threadId ? `#/threads/${encodeURIComponent(route.threadId)}` : '#/threads';

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
