import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { ControlsPage } from './playground/controls-page';

describe('portal shell', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter(routes)] }));
  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('has a skip link, page navigation and a toast outlet', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('a.skip-link')!.getAttribute('href')).toBe('#main');
    expect([...root.querySelectorAll('nav a')].map((a) => a.textContent!.trim())).toEqual([
      'Controls',
      'Data grid',
    ]);
    expect(root.querySelector('iam-toast-outlet')).not.toBeNull();
  });

  it('switches theme on <html> through the theme dropdown, without browser storage', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const combobox = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '.theme-select [role=combobox]',
    )!;
    expect(combobox.getAttribute('aria-label')).toBe('Theme');
    const key = async (k: string) => {
      combobox.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
      await fixture.whenStable();
    };
    await key('ArrowDown');
    await key('End');
    await key('Enter');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    await key('ArrowDown');
    await key('Home');
    await key('Enter');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});

describe('controls playground', () => {
  async function setup() {
    const fixture = TestBed.createComponent(ControlsPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const submit = async () => {
      root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
      await new Promise((r) => setTimeout(r, 350));
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const type = (selector: string, value: string) => {
      const input = root.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    return { fixture, root, submit, type };
  }

  it('lists client errors in the summary when submitted empty', async () => {
    const { root, submit } = await setup();
    await submit();
    const summary = root.querySelector('.iam-summary')!;
    expect(summary.textContent).toContain('Display name: is required');
    expect(summary.textContent).toContain('Company: is required');
    expect(root.textContent).toContain('Display name is required.');
  });

  it('shows ProblemDetails errors on the matching field', async () => {
    const { fixture, root, submit, type } = await setup();
    const page = fixture.componentInstance as unknown as { form: { patchValue(v: object): void } };
    page.form.patchValue({ company: '0100', level: 'read', accept: true });
    type('iam-textbox input', 'taken');
    await submit();
    expect(root.textContent).toContain('That display name is already in use.');
    expect(root.querySelector('iam-textbox input')!.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('.iam-summary')!.textContent).toContain(
      'One or more validation errors occurred.',
    );
    // The API's field name is mapped to the form's own label and links to the field.
    const link = root.querySelector<HTMLButtonElement>('.iam-summary__link')!;
    expect(link.textContent).toContain('Display name: That display name is already in use.');
    expect(link.textContent).not.toContain('DisplayName');
    link.click();
    expect(document.activeElement).toBe(root.querySelector('#displayName'));
  });
});
