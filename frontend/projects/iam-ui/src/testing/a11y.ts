import axe from 'axe-core';

/**
 * Runs axe-core (WCAG 2.0/2.1/2.2 A and AA rules) on an element and returns the moderate, serious and critical violations.
 * Colour contrast cannot be computed in jsdom (no layout); it is covered by the token contrast tests instead.
 */
export async function a11yViolations(element: Element): Promise<string[]> {
  const result = await axe.run(element, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
    rules: { 'color-contrast': { enabled: false } },
  });
  return result.violations
    .filter((v) => v.impact === 'moderate' || v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
}

/** A string that would run script if it were ever interpreted as HTML. */
export const XSS_PAYLOAD = '<img src=x onerror="globalThis.__iamXss=1">';
