const numbers = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
const percents = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });

/** 123456 → "123,456". */
export const fmt = (value: number): string => numbers.format(value);

/** 86.25 → "86.3%". */
export const pct = (value: number): string => `${percents.format(value)}%`;

/** "BelowThreshold" → "Below threshold". The host sends enum names; people read words. */
/** A running time as a clock shows it: "0:07", "1:42", "12:05". Negative counts as zero. */
export function elapsed(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

export function words(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** "Priya Raman" → "PR"; "admin" → "AD". */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  const first = parts[0]!;
  const letters = parts.length === 1 ? first.slice(0, 2) : first.charAt(0) + parts[parts.length - 1]!.charAt(0);
  return letters.toUpperCase();
}

/** The first seven characters of a commit, as git and GitHub show it. */
export const shortSha = (sha: string): string => sha.slice(0, 7);

export type Tone = 'done' | 'bad' | 'you' | 'neutral';

/** The colour a verification result is shown in. Only something that ran and passed is green. */
export function toneOf(status: string): Tone {
  switch (status) {
    case 'Passed':
    case 'Met':
    case 'Clean':
    case 'FindingsFixed':
      return 'done';
    case 'Failed':
    case 'BelowThreshold':
    case 'FindingsOpen':
      return 'bad';
    case 'Unavailable':
    case 'NoTests':
    case 'DidNotFinish':
      return 'you';
    default:
      return 'neutral';
  }
}
