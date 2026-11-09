import { expect, Page, test as base } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/** Every test fails if the page reports a CSP violation, an uncaught error or a console error. */
export const test = base.extend<{ problems: string[] }>({
  problems: [
    async ({ page }, use) => {
      const problems: string[] = [];
      page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
      page.on('console', (m) => {
        if (m.type() === 'error') {
          problems.push(`console: ${m.text()}`);
        }
      });
      await page.addInitScript(() => {
        document.addEventListener('securitypolicyviolation', (e) => {
          console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`);
        });
      });
      await use(problems);
      expect(problems, 'no CSP violations, page errors or console errors').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };

// Playwright runs from the frontend folder (playwright.config.ts).
const axeSource = readFileSync(
  join(process.cwd(), 'node_modules', 'axe-core', 'axe.min.js'),
  'utf8',
);

/**
 * Runs axe-core in the real browser, colour contrast included. Injected through the test channel (not a <script>),
 * so the page's CSP stays strict.
 */
export async function axeViolations(page: Page): Promise<string[]> {
  await page.evaluate(axeSource);
  return page.evaluate(async () => {
    const axe = (
      globalThis as unknown as { axe: { run: (c: unknown, o: unknown) => Promise<AxeResult> } }
    ).axe;
    const result = await axe.run(document, {
      runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
    });
    return result.violations
      .filter((v) => v.impact !== 'minor')
      .map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
  });
}

interface AxeResult {
  violations: { id: string; help: string; impact: string | null; nodes: { target: string[] }[] }[];
}

/** Sets the theme the way the theme dropdown does (data-theme on <html>). */
export async function useTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
}

/** The page must not scroll sideways (phone widths). */
export async function hasHorizontalScroll(page: Page): Promise<boolean> {
  return page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  );
}

/**
 * Tabs through every stop on the page and lists focused elements without a visible focus indicator: an outline of at
 * least 2px, or a box-shadow or border that changes when the element gains focus. Leaving the page for the browser's own UI and
 * coming back to the first stop marks the end; focus landing on <body> anywhere else is a gap (focus was lost).
 */
export async function focusIndicatorGaps(page: Page, maxTabs: number): Promise<string[]> {
  const gaps: string[] = [];
  let first: string | null = null;
  for (let i = 0; i < maxTabs; i++) {
    await page.keyboard.press('Tab');
    const info = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body || el === document.documentElement) {
        return { key: '<body>', name: '<body> (focus lost)', ok: false, body: true };
      }
      const focused = getComputedStyle(el);
      const ring = focused.outlineStyle !== 'none' && parseFloat(focused.outlineWidth) >= 2;
      const focusedShadow = focused.boxShadow;
      const focusedBorder = `${focused.borderTopColor} ${focused.borderTopWidth}`;
      el.blur();
      const unfocused = getComputedStyle(el);
      const unfocusedShadow = unfocused.boxShadow;
      const unfocusedBorder = `${unfocused.borderTopColor} ${unfocused.borderTopWidth}`;
      el.focus({ preventScroll: true });
      const shadow = focusedShadow !== 'none' && focusedShadow !== unfocusedShadow;
      // A border that appears or changes colour on focus (AG Grid's focused cell) also counts.
      const border = parseFloat(focused.borderTopWidth) >= 1 && focusedBorder !== unfocusedBorder;
      const name =
        el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 40) ?? el.tagName;
      return {
        key: `${el.tagName}#${el.id}|${name}`,
        name,
        ok: ring || shadow || border,
        body: false,
      };
    });
    if (info.body) {
      await page.keyboard.press('Tab');
      const back = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement;
        const name =
          el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 40) ?? el.tagName;
        return `${el.tagName}#${el.id}|${name}`;
      });
      if (back === first) {
        return gaps;
      }

      gaps.push(info.name);
      continue;
    }

    first ??= info.key;
    if (!info.ok) {
      gaps.push(info.name);
    }
  }

  return gaps;
}
