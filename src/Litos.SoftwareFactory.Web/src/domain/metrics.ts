/** How the Metrics tab writes its figures (m4-architecture.md §6, §12). */

/** Hours as people read them: minutes under an hour, days over two. */
export const duration = (hours: number): string =>
  hours < 1 ? `${Math.round(hours * 60)} min` : hours < 48 ? `${hours.toFixed(1)} h` : `${(hours / 24).toFixed(1)} days`;

/** A share as a whole percentage; "—" when there is nothing to share. */
export const percent = (share: number | null): string => (share === null ? '—' : `${Math.round(share * 100)}%`);

/** 412300 → "412k"; 1084000 → "1.08M"; 14620000 → "14.6M". */
export const tokens = (n: number): string =>
  n >= 1e6 ? `${(n / 1e6).toFixed(n >= 1e7 ? 1 : 2)}M` : n >= 1e3 ? `${Math.round(n / 1e3)}k` : String(Math.round(n));

/** State and stage names as the app shows them elsewhere: "AwaitingHumanTesting" → "Awaiting Human Testing". */
export const label = (name: string): string => name.replace(/([a-z])([A-Z])/g, '$1 $2');
