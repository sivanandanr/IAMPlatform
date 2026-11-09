import { readFile } from 'node:fs/promises';
import {
  axeViolations,
  expect,
  focusIndicatorGaps,
  hasHorizontalScroll,
  test,
  useTheme,
} from './fixtures';

test.describe('data grid page in a real browser', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/grid');
    await expect(page.getByText('1–10 of 137')).toBeVisible();
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`has no axe violations, colour contrast included (${theme})`, async ({ page }) => {
      await useTheme(page, theme);
      expect(await axeViolations(page)).toEqual([]);
    });
  }

  test('AG Grid styles load under the strict CSP (nonce applied)', async ({ page }) => {
    const unstyled = await page
      .locator('.ag-root-wrapper')
      .evaluate((el) => getComputedStyle(el).display === 'inline');
    expect(unstyled).toBe(false);
  });

  test('filters by option and text, from page 1', async ({ page }) => {
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByText('11–20 of 137')).toBeVisible();
    await page.getByRole('combobox', { name: 'Application' }).click();
    await page.getByRole('option', { name: 'Payroll' }).click();
    await page.getByRole('textbox', { name: 'Entitlement contains' }).fill('  role 1  ');
    await page.getByRole('button', { name: 'Apply filters' }).click();
    // Payroll rows are entitlements 4, 9, 14, …; those containing "role 1": 14, 19, 104, 109, … 134.
    await expect(page.getByText('1–9 of 9', { exact: true })).toBeVisible();
    const apps = await page.locator('.ag-cell[col-id="application"]').allTextContents();
    expect(apps.length).toBeGreaterThan(0);
    expect(new Set(apps.map((a) => a.trim()))).toEqual(new Set(['Payroll']));

    await page.getByRole('button', { name: 'Clear filters' }).click();
    await expect(page.getByText('1–10 of 137')).toBeVisible();
  });

  test('exports a server-built CSV with formula cells neutralized', async ({ page }) => {
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export CSV' }).click();
    const file = await download;
    expect(file.suggestedFilename()).toBe('entitlements.csv');
    const text = await readFile((await file.path())!, 'utf8');
    const lines = text.split('\r\n');
    expect(lines[0]).toBe('Code,Entitlement,Application,Owner');
    expect(lines).toHaveLength(101);
    expect(lines[1]).toMatch(/^0001,/);
    // Row 0100's owner starts with "=": the server-side rule must have neutralized it (leading quote, RFC 4180 quoting).
    const row100 = lines.find((l) => l.startsWith('0100,'))!;
    expect(row100).toContain(`"'=HYPERLINK(""https://example.invalid"",""Payroll office"")"`);
    expect(lines.filter((l) => /(^|,)[=+\-@]/.test(l))).toEqual([]);
  });

  test('every focusable control on the grid page shows a visible focus indicator', async ({
    page,
  }) => {
    expect(await focusIndicatorGaps(page, 40)).toEqual([]);
  });

  test('pages with the keyboard and announces the range', async ({ page }) => {
    const next = page.getByRole('button', { name: 'Next', exact: true });
    await next.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('status').filter({ hasText: 'page 2 of 14' })).toBeAttached();
  });

  test('fits a phone screen without sideways page scrolling', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    await page.reload();
    await expect(page.getByText('1–10 of 137')).toBeVisible();
    expect(await hasHorizontalScroll(page)).toBe(false);
  });
});
