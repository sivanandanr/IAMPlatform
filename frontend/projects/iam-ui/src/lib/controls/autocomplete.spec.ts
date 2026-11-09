import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamFormField } from '../forms/form-field';
import { IamTooltip } from '../overlays/tooltip';
import {
  IAM_AUTOCOMPLETE_MAX_QUERY,
  IAM_AUTOCOMPLETE_MAX_RESULTS,
  IamAutocomplete,
  IamAutocompletePage,
  IamAutocompleteSearch,
} from './autocomplete';

interface Call {
  readonly query: string;
  readonly signal: AbortSignal;
  resolve(page: IamAutocompletePage<string>): void;
  reject(error: unknown): void;
}

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamAutocomplete, IamTooltip],
  template: `
    <iam-form-field label="Manager" controlId="manager">
      <iam-autocomplete
        [formControl]="manager"
        [search]="search"
        [debounceMs]="0"
        [selectedOption]="selectedOption()"
      />
    </iam-form-field>
    <button type="button" id="help" iamTooltip="Managers come from the HR system">Help</button>
  `,
})
class Host {
  readonly calls: Call[] = [];
  readonly selectedOption = signal<{ value: string; label: string } | null>(null);
  readonly manager = new FormControl<string | null>(null);
  readonly search: IamAutocompleteSearch<string> = (query, signal) =>
    new Promise((resolve, reject) => this.calls.push({ query, signal, resolve, reject }));
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
  const input = root.querySelector<HTMLInputElement>('#manager')!;
  const tick = async () => {
    await new Promise((r) => setTimeout(r, 5));
    await fixture.whenStable();
  };
  const type = async (text: string) => {
    input.focus();
    input.value = text;
    input.dispatchEvent(new Event('input'));
    await tick();
  };
  const key = async (k: string) => {
    input.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
    await tick();
  };
  const options = () =>
    [...document.querySelectorAll<HTMLElement>('[role=option]')].map((o) => o.textContent!.trim());
  const page = (labels: string[], hasMore = false): IamAutocompletePage<string> => ({
    items: labels.map((label) => ({ value: label.toLowerCase().replace(/\s+/g, '.'), label })),
    hasMore,
  });
  return { fixture, root, input, type, key, tick, options, page, host: fixture.componentInstance };
}

describe('iam-autocomplete', () => {
  it('is a labelled combobox that does not search below the minimum or for "*"', async () => {
    const { input, type, host } = await setup();
    expect(input.getAttribute('role')).toBe('combobox');
    expect(input.getAttribute('aria-autocomplete')).toBe('list');
    await type('a');
    await type('*');
    await type('**');
    expect(host.calls).toEqual([]);
    expect(document.querySelector('.iam-ac__panel')!.textContent).toContain(
      'Type at least 2 characters',
    );
  });

  it('caps the query, aborts superseded requests and ignores late responses', async () => {
    const { type, tick, options, page, host } = await setup();
    await type('ad');
    await type('ada');
    expect(host.calls.map((c) => c.query)).toEqual(['ad', 'ada']);
    expect(host.calls[0].signal.aborted).toBe(true);

    host.calls[1].resolve(page(['Ada Lovelace']));
    await tick();
    host.calls[0].resolve(page(['Adam Smith', 'Adele Adkins']));
    await tick();
    expect(options()).toEqual(['Ada Lovelace']);

    await type('x'.repeat(500));
    expect(host.calls.at(-1)!.query.length).toBe(IAM_AUTOCOMPLETE_MAX_QUERY);
  });

  it('shows only the first page, capped, and asks to refine', async () => {
    const { type, tick, options, page, host } = await setup();
    await type('sm');
    host.calls[0].resolve(
      page(
        Array.from({ length: 80 }, (_, i) => `Smith ${i}`),
        true,
      ),
    );
    await tick();
    expect(options()).toHaveLength(IAM_AUTOCOMPLETE_MAX_RESULTS);
    expect(document.querySelector('.iam-ac__panel')!.textContent).toContain('Refine your search');
  });

  it('selects with the keyboard and keeps text and value in step', async () => {
    const { input, type, key, tick, page, host } = await setup();
    await type('gr');
    host.calls[0].resolve(page(['Grace Hopper', 'Greta Garbo']));
    await tick();
    expect(input.getAttribute('aria-expanded')).toBe('true');
    // Nothing is highlighted until the user moves (Enter alone does not pick).
    expect(input.hasAttribute('aria-activedescendant')).toBe(false);
    await key('ArrowDown');
    expect(input.getAttribute('aria-activedescendant')).toBe(
      document.querySelectorAll('[role=option]')[0].id,
    );
    await key('ArrowDown');
    await key('Enter');
    expect(host.manager.value).toBe('greta.garbo');
    expect(input.value).toBe('Greta Garbo');
    expect(input.getAttribute('aria-expanded')).toBe('false');

    // Editing the text clears the value; leaving restores nothing that does not match.
    await type('Greta G');
    expect(host.manager.value).toBeNull();
    input.dispatchEvent(new Event('blur'));
    await tick();
    expect(input.value).toBe('');
    expect(host.manager.touched).toBe(true);
  });

  it('restores the selected label when leaving with half-typed text', async () => {
    const { fixture, input, tick, host } = await setup();
    host.selectedOption.set({ value: 'ada.lovelace', label: 'Ada Lovelace' });
    host.manager.setValue('ada.lovelace');
    await fixture.whenStable();
    expect(input.value).toBe('Ada Lovelace');
    input.dispatchEvent(new Event('blur'));
    await tick();
    expect(input.value).toBe('Ada Lovelace');
    expect(host.manager.value).toBe('ada.lovelace');
  });

  it('closes on Escape and aborts the pending request', async () => {
    const { input, type, key, host } = await setup();
    await type('jo');
    await key('Escape');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(host.calls[0].signal.aborted).toBe(true);
  });

  it('announces errors and empty results, and renders labels as text', async () => {
    const { root, type, tick, page, host } = await setup();
    await type('zz');
    host.calls[0].reject(new Error('500'));
    await tick();
    expect(root.querySelector('[role=status]')!.textContent).toContain('Search failed');
    await type('zzz');
    host.calls[1].resolve(page([]));
    await tick();
    expect(root.querySelector('[role=status]')!.textContent).toContain('No matches');
    await type('xs');
    host.calls[2].resolve({ items: [{ value: 'x', label: XSS_PAYLOAD }], hasMore: false });
    await tick();
    expect(document.querySelector('.iam-ac__panel img')).toBeNull();
    expect(document.querySelector('[role=option]')!.textContent).toContain(XSS_PAYLOAD);
  });

  it('has no accessibility violations, closed or open', async () => {
    const { root, type, tick, page, host } = await setup();
    expect(await a11yViolations(root)).toEqual([]);
    await type('gr');
    host.calls[0].resolve(page(['Grace Hopper']));
    await tick();
    expect(await a11yViolations(document.body)).toEqual([]);
  });
});

describe('iamTooltip', () => {
  it('shows on focus, describes its host, and hides on Escape', async () => {
    const { root, tick } = await setup();
    const help = root.querySelector<HTMLButtonElement>('#help')!;
    help.focus();
    help.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    await tick();
    const tooltip = document.querySelector<HTMLElement>('[role=tooltip]')!;
    expect(tooltip.textContent).toContain('Managers come from the HR system');
    expect(help.getAttribute('aria-describedby')).toBe(tooltip.id);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await tick();
    expect(document.querySelector('[role=tooltip]')).toBeNull();
    expect(help.hasAttribute('aria-describedby')).toBe(false);
  });
});
