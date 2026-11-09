import { expect, test, useTheme } from './fixtures';

/**
 * Screenshot baselines (run with IAM_VISUAL=1). Baselines are stored per platform under e2e/__screenshots__/; review
 * every baseline change as you would a code change. Data is synthetic and the clock is fixed, so pages are stable.
 */
const sizes = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'phone', width: 390, height: 844 },
] as const;

test.beforeEach(async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-06-15T10:00:00Z'));
});

for (const size of sizes) {
  for (const theme of ['light', 'dark'] as const) {
    test(`controls ${size.name} ${theme}`, async ({ page }) => {
      await page.setViewportSize(size);
      await page.goto('/');
      await useTheme(page, theme);
      await expect(page).toHaveScreenshot(`controls-${size.name}-${theme}.png`, { fullPage: true });
    });

    test(`grid ${size.name} ${theme}`, async ({ page }) => {
      await page.setViewportSize(size);
      await page.goto('/grid');
      await expect(page.getByText('1–10 of 137')).toBeVisible();
      await useTheme(page, theme);
      await expect(page).toHaveScreenshot(`grid-${size.name}-${theme}.png`, { fullPage: true });
    });
  }
}

test('controls with forced colours', async ({ page }) => {
  await page.emulateMedia({ forcedColors: 'active' });
  await page.goto('/');
  await expect(page).toHaveScreenshot('controls-forced-colors.png', { fullPage: true });
});
