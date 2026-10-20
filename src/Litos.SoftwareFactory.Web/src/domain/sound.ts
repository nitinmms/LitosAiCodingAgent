/**
 * The "meal ready" sound played when a task starts waiting on you: three short beeps, like a
 * microwave that has finished. Made with the Web Audio API, so there is no sound file to ship.
 *
 * Browsers start audio only after the person has interacted with the page; signing in counts. A
 * page reopened with a session already signed in stays silent until its first click.
 */

const STORAGE_KEY = 'litos.factory.sound';

/** On unless this browser was told otherwise: the person asked for it, and the top bar turns it off. */
export function soundEnabled(storage: Pick<Storage, 'getItem'> | undefined = safeStorage()): boolean {
  try {
    return storage?.getItem(STORAGE_KEY) !== 'off';
  } catch {
    return true;
  }
}

export function setSoundEnabled(on: boolean, storage: Pick<Storage, 'setItem'> | undefined = safeStorage()): void {
  try {
    storage?.setItem(STORAGE_KEY, on ? 'on' : 'off');
  } catch {
    // Private windows and blocked storage: the choice lasts until the page is closed.
  }
}

function safeStorage(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}

/** The beeps, as start offsets in seconds; each lasts BEEP_SECONDS. */
export const BEEPS = [0, 0.32, 0.64] as const;
export const BEEP_SECONDS = 0.2;
const PITCH_HZ = 1_400;
const VOLUME = 0.18;

let context: AudioContext | null = null;

/** Plays the three beeps. Does nothing where the browser has no Web Audio, or will not start it yet. */
export function playMealReady(): void {
  const Context = typeof window === 'undefined' ? undefined : (window.AudioContext ?? (window as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext);
  if (!Context) return;
  try {
    context ??= new Context();
    // Suspended until the page has been interacted with; resuming then succeeds.
    if (context.state === 'suspended') void context.resume().catch(() => undefined);
    const now = context.currentTime;
    for (const offset of BEEPS) beep(context, now + offset);
  } catch {
    // A sound that cannot play must never break the board.
  }
}

function beep(audio: AudioContext, start: number): void {
  const tone = audio.createOscillator();
  const level = audio.createGain();
  tone.type = 'triangle';
  tone.frequency.value = PITCH_HZ;
  // A quick rise and fall, so the beeps do not click.
  level.gain.setValueAtTime(0, start);
  level.gain.linearRampToValueAtTime(VOLUME, start + 0.01);
  level.gain.setValueAtTime(VOLUME, start + BEEP_SECONDS - 0.04);
  level.gain.linearRampToValueAtTime(0, start + BEEP_SECONDS);
  tone.connect(level).connect(audio.destination);
  tone.start(start);
  tone.stop(start + BEEP_SECONDS);
}
