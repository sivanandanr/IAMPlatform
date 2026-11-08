import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { CONTRAST_PAIRS, contrastRatio, readColorTokens } from './contrast';

const css = readFileSync(resolve(process.cwd(), 'projects/iam-ui/src/styles/tokens.css'), 'utf8');

describe('design tokens', () => {
  for (const [theme, selector] of [
    ['light', ':root'],
    ['dark', ":root[data-theme='dark']"],
  ] as const) {
    it(`meet WCAG 2.2 AA contrast in the ${theme} theme`, () => {
      const tokens = readColorTokens(css, selector);
      const failures = CONTRAST_PAIRS.filter(({ fg, bg }) => tokens[fg] && tokens[bg])
        .map(({ fg, bg, min }) => ({ fg, bg, min, ratio: contrastRatio(tokens[fg], tokens[bg]) }))
        .filter(({ ratio, min }) => ratio < min)
        .map(({ fg, bg, min, ratio }) => `${fg} on ${bg}: ${ratio.toFixed(2)} < ${min}`);

      expect(failures).toEqual([]);
      expect(CONTRAST_PAIRS.every(({ fg, bg }) => tokens[fg] && tokens[bg])).toBe(true);
    });
  }

  it('uses the same dark colours for the OS preference and the explicit dark theme', () => {
    expect(readColorTokens(css, ":root:not([data-theme='light'])")).toEqual(
      readColorTokens(css, ":root[data-theme='dark']"),
    );
  });

  it('turns motion off when the user asks for reduced motion', () => {
    expect(css).toMatch(
      /prefers-reduced-motion: reduce\)\s*{\s*:root\s*{\s*--iam-motion-duration: 0ms;/,
    );
  });

  it('computes known contrast ratios', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 1);
    expect(contrastRatio('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
    expect(() => contrastRatio('red', '#ffffff')).toThrow();
  });
});
