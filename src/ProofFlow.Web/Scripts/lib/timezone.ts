/**
 * Tells the server which time zone the reader is in.
 *
 * The server has no way to know. It used to render timestamps in its own zone, which on a hosted
 * installation is UTC — so a run started at 09:00 in Tehran appeared as 05:30 to the person who
 * started it, in an audit log whose whole purpose is establishing when things happened.
 *
 * A cookie rather than a header, because the value has to be present on an ordinary page render,
 * not just on fetch calls. The first load of a session has no cookie yet and falls back to UTC;
 * `Dates.ZoneIsAssumed` makes that visible rather than letting a wrong time look like a fact.
 */

const COOKIE = 'proofflow.tz';

export function reportTimeZone(): void {
  let zone: string | undefined;

  try {
    zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  } catch {
    // Some hardened browsers refuse to resolve a zone. UTC on the server is the honest answer.
    return;
  }

  if (!zone) return;

  const encoded = encodeURIComponent(zone);
  if (readCookie() === encoded) return;

  // A year, refreshed whenever it changes — a laptop that crosses a border updates on the next
  // page load rather than at the end of the trip.
  document.cookie = `${COOKIE}=${encoded}; path=/; max-age=31536000; samesite=lax`;

  // Nothing on screen is re-rendered. Every timestamp already carries its UTC value in the
  // datetime attribute, so the page the reader is looking at is not wrong — only the zone it
  // chose to display in, which corrects itself on the next navigation. Reloading here to fix a
  // few minutes of offset would be a worse trade than waiting.
}

function readCookie(): string | null {
  for (const part of document.cookie.split(';')) {
    const [name, ...rest] = part.trim().split('=');
    if (name === COOKIE) return rest.join('=');
  }
  return null;
}
