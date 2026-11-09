/**
 * CSV helpers (S1-10, ADR-0019). Exports are produced and authorized on the server; these functions are the reference
 * rule the server must apply (and what the playground's fake server uses), not a client-side export.
 *
 * Formula injection (OWASP "CSV Injection"): a cell starting with = + - @, a tab, carriage return or line feed can be
 * run as a formula by spreadsheet software. Leading spaces are ignored when deciding (some imports trim them), and the
 * full-width forms ＝ ＋ － ＠ count too (some locales convert them). Such cells are prefixed with a single quote,
 * which spreadsheets show as text. The server applies the same rule to every exported cell.
 */
const FORMULA_START = /^(?:[\t\r\n]|[\s\u3000]*[=+\-@\uFF1D\uFF0B\uFF0D\uFF20])/;
/** Device names Windows will not open as files, with or without an extension. */
const WINDOWS_RESERVED = /^(?:con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i;

/** Makes one cell value safe to open in a spreadsheet: formula-looking text gets a leading single quote. */
export function neutralizeCsvCell(value: unknown): string {
  const text = value === null || value === undefined ? '' : String(value);
  return FORMULA_START.test(text) ? `'${text}` : text;
}

/** One RFC 4180 field: neutralized, then quoted if it contains a comma, quote or line break (quotes doubled). */
export function toCsvField(value: unknown): string {
  const text = neutralizeCsvCell(value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** One CSV line (CRLF is added by the caller between lines, per RFC 4180). */
export function toCsvRow(values: readonly unknown[]): string {
  return values.map(toCsvField).join(',');
}

/**
 * A safe download name: letters, digits, '.', '-' and '_' only, at most 100 characters, ending in `.csv`, never a
 * Windows device name (CON, NUL, COM1, …). Anything
 * else (path separators, control characters, reserved names) is replaced, so a server-supplied name cannot escape.
 */
export function safeCsvFileName(name: string | null | undefined): string {
  const base = (name ?? '')
    .replace(/\.csv$/i, '')
    .replace(/[^A-Za-z0-9._-]+/g, '_')
    .replace(/^[._-]+/, '')
    .slice(0, 96);
  const safe = WINDOWS_RESERVED.test(base) ? `export_${base}` : base;
  return `${safe || 'export'}.csv`;
}
