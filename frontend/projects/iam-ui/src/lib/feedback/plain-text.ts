/**
 * Single-line plain text for messages from servers or callers: control characters become spaces, invisible format
 * characters (bidi overrides, zero-width characters, BOM) are removed so text cannot be visually spoofed, and the
 * result is cut to `max` characters without splitting a surrogate pair. Rendered as text by Angular, never as HTML.
 */
export function plainText(value: unknown, max: number): string {
  const text = typeof value === 'string' ? value : String(value ?? '');
  const clean = text
    .replace(/\p{Cc}/gu, ' ')
    .replace(/\p{Cf}/gu, '')
    .replace(/\s+/g, ' ')
    .trim();
  const characters = Array.from(clean);
  return characters.length > max ? `${characters.slice(0, max - 1).join('')}…` : clean;
}
