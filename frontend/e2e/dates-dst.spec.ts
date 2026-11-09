import { expect, test } from './fixtures';

/**
 * Date-only values across a daylight-saving change (S1-10 review): in New York the clocks go forward at 02:00 on
 * 8 March 2026. The calendar must still say today is 8 March and the next day 9 March, whatever the hour.
 */
test.use({ timezoneId: 'America/New_York' });

for (const instant of ['2026-03-08T06:30:00Z', '2026-03-08T07:30:00Z', '2026-03-09T03:59:00Z']) {
  test(`today and tomorrow stay whole days around the DST change (${instant})`, async ({
    page,
  }) => {
    await page.clock.setFixedTime(new Date(instant));
    await page.goto('/');
    await page.getByRole('button', { name: 'Choose date' }).first().click();
    await expect(page.locator('.iam-calendar [aria-current=date]')).toHaveAttribute(
      'aria-label',
      'March 8, 2026',
    );
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('textbox', { name: 'Start date' })).toHaveValue('03/09/2026');
  });
}
