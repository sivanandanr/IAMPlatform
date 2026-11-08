/** WCAG 2.2 relative-luminance contrast, used by the token tests (no runtime use). */
export function contrastRatio(foreground: string, background: string): number {
  const l1 = luminance(foreground);
  const l2 = luminance(background);
  const [hi, lo] = l1 >= l2 ? [l1, l2] : [l2, l1];
  return (hi + 0.05) / (lo + 0.05);
}

function luminance(hex: string): number {
  const match = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  if (!match) {
    throw new Error(`Expected a #rrggbb colour, got '${hex}'.`);
  }

  const value = match[1];
  const channel = (offset: number): number => {
    const c = parseInt(value.slice(offset, offset + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4);
}

/** Reads `--iam-color-*` hex tokens from one CSS rule block. */
export function readColorTokens(css: string, selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) {
    throw new Error(`Selector '${selector}' not found.`);
  }

  const body = css.slice(start, css.indexOf('}', start));
  const tokens: Record<string, string> = {};
  for (const m of body.matchAll(/--iam-color-([a-z-]+):\s*(#[0-9a-f]{6});/gi)) {
    tokens[m[1]] = m[2];
  }

  return tokens;
}

/** Text pairs need 4.5:1; borders, focus rings and other non-text UI need 3:1 (WCAG 1.4.3, 1.4.11). */
export const CONTRAST_PAIRS: readonly { fg: string; bg: string; min: number }[] = [
  ...['bg', 'surface', 'surface-raised'].flatMap((bg) => [
    { fg: 'text', bg, min: 4.5 },
    { fg: 'text-muted', bg, min: 4.5 },
    { fg: 'primary', bg, min: 4.5 },
    { fg: 'danger', bg, min: 4.5 },
    { fg: 'success', bg, min: 4.5 },
    { fg: 'warning', bg, min: 4.5 },
    { fg: 'info', bg, min: 4.5 },
    { fg: 'border', bg, min: 3 },
    { fg: 'focus', bg, min: 3 },
  ]),
  { fg: 'on-primary', bg: 'primary', min: 4.5 },
  { fg: 'on-primary', bg: 'primary-hover', min: 4.5 },
  { fg: 'on-danger', bg: 'danger', min: 4.5 },
  { fg: 'disabled-text', bg: 'disabled-bg', min: 4.5 },
];
