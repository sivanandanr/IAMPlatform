import {
  axeViolations,
  expect,
  focusIndicatorGaps,
  hasHorizontalScroll,
  test,
  useTheme,
} from './fixtures';

test.describe('controls page in a real browser', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1, name: 'Controls' })).toBeVisible();
  });

  test('is served with the whole strict CSP and a fresh nonce on every response', async ({
    page,
  }) => {
    const { cspFor } = (await import('../scripts/serve-e2e.mjs')) as {
      cspFor: (nonce: string) => string;
    };
    const nonces = new Set<string>();
    for (let i = 0; i < 3; i++) {
      const response = await page.request.get('/');
      const csp = response.headers()['content-security-policy'];
      const nonce = /'nonce-([^']+)'/.exec(csp)![1];
      // The exact policy, so a weakened directive (or a missing one) fails the test.
      expect(csp).toBe(cspFor(nonce));
      expect(nonce).not.toBe('IAM_CSP_NONCE');
      expect(nonce.length).toBeGreaterThanOrEqual(22);
      const body = await response.text();
      expect(body).not.toContain('IAM_CSP_NONCE');
      expect(body.toLowerCase()).toContain(`ngcspnonce="${nonce}"`.toLowerCase());
      expect(response.headers()['permissions-policy']).toContain('camera=()');
      nonces.add(nonce);
    }
    expect(nonces.size).toBe(3);

    const styleNonces = await page.evaluate(() =>
      [...document.querySelectorAll('style')].map((s) => s.nonce),
    );
    expect(styleNonces.length).toBeGreaterThan(0);
    expect(styleNonces.every((n) => n.length > 0)).toBe(true);
  });

  test('the policy is enforced: Trusted Types blocks HTML sinks and un-nonced styles are refused', async ({
    page,
    problems,
  }) => {
    const result = await page.evaluate(async () => {
      const violations: string[] = [];
      document.addEventListener('securitypolicyviolation', (e) =>
        violations.push(e.violatedDirective),
      );
      let innerHtmlBlocked = false;
      try {
        document.createElement('div').innerHTML = '<b>x</b>';
      } catch {
        innerHtmlBlocked = true;
      }
      const probe = document.createElement('p');
      probe.id = 'csp-probe';
      document.body.append(probe);
      const style = document.createElement('style');
      style.textContent = '#csp-probe { color: rgb(1, 2, 3); }';
      document.head.append(style);
      await new Promise((r) => setTimeout(r, 100));
      const styled = getComputedStyle(probe).color === 'rgb(1, 2, 3)';
      probe.remove();
      style.remove();
      return { innerHtmlBlocked, styled, violations };
    });
    expect(result.innerHtmlBlocked).toBe(true);
    expect(result.styled).toBe(false);
    expect(result.violations).toEqual(
      expect.arrayContaining(['require-trusted-types-for', 'style-src-elem']),
    );
    // These violations were provoked on purpose; any other problem still fails the test.
    const provoked =
      /CSP violation: (require-trusted-types-for|style-src-elem)|TrustedHTML|Content Security Policy/;
    const unexpected = problems.filter((p) => !provoked.test(p));
    problems.length = 0;
    problems.push(...unexpected);
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`has no axe violations, colour contrast included (${theme})`, async ({ page }) => {
      await useTheme(page, theme);
      expect(await axeViolations(page)).toEqual([]);
      // Errors shown too, so their colours are checked.
      await page.getByRole('button', { name: 'Submit' }).click();
      await expect(page.getByText('Please correct')).toBeVisible();
      expect(await axeViolations(page)).toEqual([]);
    });
  }

  test('the skip link is the first tab stop and moves focus to the main content', async ({
    page,
  }) => {
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Skip to content' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter');
    await expect(page.locator('main#main')).toBeFocused();
  });

  test('every focusable control shows a visible focus indicator', async ({ page }) => {
    expect(await focusIndicatorGaps(page, 40)).toEqual([]);
  });

  test('has no axe violations with each overlay open', async ({ page }) => {
    // Calendar with a selected date.
    const start = page.getByRole('textbox', { name: 'Start date' });
    await page.getByRole('button', { name: 'Choose date' }).first().click();
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('Enter');
    await page.getByRole('button', { name: 'Choose date' }).first().click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.locator('.iam-calendar [aria-selected=true]')).toHaveCount(1);
    expect(await axeViolations(page)).toEqual([]);
    await page.keyboard.press('Escape');
    await expect(start).not.toHaveValue('');

    // Dropdown listbox.
    await page.getByRole('combobox', { name: 'Company' }).focus();
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('listbox')).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
    await page.keyboard.press('Escape');

    // Autocomplete results with an active option.
    await page.getByRole('combobox', { name: 'Line manager' }).fill('pat');
    await expect(page.getByRole('option').first()).toBeVisible();
    await page.keyboard.press('ArrowDown');
    expect(await axeViolations(page)).toEqual([]);
    await page.keyboard.press('Escape');

    // Confirm dialog.
    await page.getByRole('button', { name: 'Reset form' }).click();
    await expect(page.getByRole('alertdialog')).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
  });

  test('axe checks wait for a running fade, so they measure final colours', async ({ page }) => {
    // Long enough that the fade is still visible when axe measures, or this test can't fail.
    await page.evaluate(() =>
      document.documentElement.style.setProperty('--iam-motion-duration', '3000ms'),
    );
    await page.getByRole('button', { name: 'Reset form' }).click();
    await expect(page.getByRole('alertdialog')).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
  });

  test('axe checks do not wait for endless animations', async ({ page }) => {
    await page.evaluate(() => {
      document.body.animate([{ opacity: 1 }, { opacity: 1 }], {
        duration: 1000,
        iterations: Infinity,
      });
    });
    expect(await axeViolations(page)).toEqual([]);
  });

  test('dropdown works with the keyboard alone', async ({ page }) => {
    const company = page.getByRole('combobox', { name: 'Company' });
    await company.focus();
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('listbox')).toBeVisible();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(company).toContainText('Bravo Ltd (0200)');
    await expect(page.getByRole('listbox')).toBeHidden();
    await expect(company).toBeFocused();
  });

  test('confirm dialog traps focus and returns it on Escape', async ({ page }) => {
    const reset = page.getByRole('button', { name: 'Reset form' });
    await reset.click();
    const dialog = page.getByRole('alertdialog', { name: 'Reset the form?' });
    await expect(dialog).toBeVisible();
    for (let i = 0; i < 6; i++) {
      await page.keyboard.press('Tab');
      expect(await dialog.evaluate((d) => d.contains(document.activeElement))).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(reset).toBeFocused();
  });

  test('date picker: keyboard calendar, typed dates and the minimum date', async ({ page }) => {
    const input = page.getByRole('textbox', { name: 'Start date' });
    await page.getByRole('button', { name: 'Choose date' }).first().click();
    const calendar = page.getByRole('dialog');
    await expect(calendar).toBeVisible();
    // The day after the one the calendar marks as today (read from the page, so midnight cannot split them).
    const todayLabel = await page
      .locator('.iam-calendar [aria-current=date]')
      .getAttribute('aria-label');
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('Enter');
    await expect(calendar).toBeHidden();
    const tomorrow = await page.evaluate((label) => {
      const d = new Date(`${label} 12:00`);
      d.setDate(d.getDate() + 1);
      return d.toLocaleDateString('en-US', { day: '2-digit', month: '2-digit', year: 'numeric' });
    }, todayLabel!);
    await expect(input).toHaveValue(tomorrow);

    await input.fill('01/01/2000');
    await input.blur();
    await expect(page.getByText(/The date must be on or after/)).toBeVisible();
    await expect(input).toHaveAttribute('aria-invalid', 'true');

    await input.fill('02/31/2030');
    await input.blur();
    await expect(page.getByText(/The date is not valid|Enter a real date/)).toBeVisible();
  });

  test('autocomplete: search, keyboard choice, and a search error', async ({ page }) => {
    const box = page.getByRole('combobox', { name: 'Line manager' });
    await box.fill('p');
    await expect(page.getByText('Type at least 2 characters to search.')).toBeVisible();
    await box.fill('pat');
    const listbox = page.getByRole('listbox');
    await expect(listbox.getByRole('option').first()).toBeVisible();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(box).toHaveValue(/Patel \(E\d{5}\)/);
    await expect(listbox).toBeHidden();

    await box.fill('fail');
    await expect(
      page.locator('.cdk-overlay-container').getByText('Search failed. Try again.'),
    ).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Search failed' })).toBeAttached();
  });

  test('tooltip shows on focus, is announced, and Escape hides it', async ({ page }) => {
    const trigger = page.getByRole('button', { name: 'About access periods' });
    await trigger.focus();
    const tip = page.getByRole('tooltip');
    await expect(tip).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-describedby', (await tip.getAttribute('id'))!);
    await page.keyboard.press('Escape');
    await expect(tip).toBeHidden();
  });

  test('forced colours keep focus indicators and control borders', async ({ page }) => {
    await page.emulateMedia({ forcedColors: 'active' });
    const submit = page.getByRole('button', { name: 'Submit' });
    await submit.focus();
    const outline = await submit.evaluate((el) => getComputedStyle(el).outlineStyle);
    expect(outline).not.toBe('none');
    const border = await page
      .getByRole('textbox', { name: 'Display name' })
      .evaluate((el) => getComputedStyle(el).borderTopStyle);
    expect(border).not.toBe('none');
  });

  test('fits a phone screen without sideways scrolling', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Controls' })).toBeVisible();
    expect(await hasHorizontalScroll(page)).toBe(false);
  });
});
