import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { IamFormField } from '../forms/form-field';
import { IamOption } from './choice';
import { IamAutocomplete, IamAutocompletePage, IamAutocompleteSearch } from './autocomplete';

/** Regression tests for the S1-10 independent review findings on iam-autocomplete. */

interface Call {
  readonly query: string;
  resolve(page: IamAutocompletePage<string>): void;
}

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamAutocomplete],
  template: `
    <iam-form-field label="Approver" controlId="approver">
      <iam-autocomplete
        [formControl]="approver"
        [search]="search"
        [debounceMs]="debounce()"
        [minChars]="minChars()"
        [selectedOption]="selectedOption()"
      />
    </iam-form-field>
  `,
})
class Host {
  readonly calls: Call[] = [];
  readonly debounce = signal(0);
  readonly minChars = signal(2);
  readonly selectedOption = signal<IamOption<string> | null>(null);
  readonly approver = new FormControl<string | null>(null);
  readonly search: IamAutocompleteSearch<string> = (query) =>
    new Promise((resolve) => this.calls.push({ query, resolve }));
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
  const input = root.querySelector<HTMLInputElement>('#approver')!;
  const tick = async (ms = 5) => {
    await new Promise((r) => setTimeout(r, ms));
    await fixture.whenStable();
  };
  const type = async (text: string, wait = 5) => {
    input.focus();
    input.value = text;
    input.dispatchEvent(new Event('input'));
    await tick(wait);
  };
  const key = async (k: string) => {
    input.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
    await tick();
  };
  const page = (...people: [string, string][]): IamAutocompletePage<string> => ({
    items: people.map(([value, label]) => ({ value, label })),
    hasMore: false,
  });
  return { fixture, input, type, key, tick, page, host: fixture.componentInstance };
}

describe('iam-autocomplete after review', () => {
  it('never commits a result from an older query when Enter is pressed during the next search', async () => {
    const { input, type, key, tick, page, host } = await setup();
    await type('pat');
    host.calls[0].resolve(page(['E1', 'Pat Patel'], ['E2', 'Pat Smith']));
    await tick();
    await key('ArrowDown');
    expect(input.getAttribute('aria-activedescendant')).not.toBeNull();

    host.debounce.set(250);
    await tick();
    await type('patt');
    await key('Enter');
    expect(host.approver.value).toBeNull();
    expect(document.querySelector('[role=listbox]')).toBeNull();
    expect(input.hasAttribute('aria-activedescendant')).toBe(false);
  });

  it('does not pick the first result when Enter is pressed without choosing', async () => {
    const { key, tick, type, page, host } = await setup();
    await type('pat');
    host.calls[0].resolve(page(['E1', 'Pat Patel']));
    await tick();
    await key('Enter');
    expect(host.approver.value).toBeNull();
  });

  it('cannot be configured to search empty or one-character text (DL-027)', async () => {
    const { input, type, key, host } = await setup();
    host.minChars.set(0);
    await type('');
    await key('ArrowDown');
    await type('a');
    expect(host.calls).toEqual([]);
    expect(document.querySelector('.iam-ac__panel')!.textContent).toContain('at least 2');
    host.minChars.set(-5);
    await type('');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(host.calls).toEqual([]);
  });

  it("shows a form-written option's label only next to that option's value", async () => {
    const { fixture, input, host } = await setup();
    host.selectedOption.set({ value: 'E1', label: 'Pat Patel' });
    host.approver.setValue('E1');
    await fixture.whenStable();
    expect(input.value).toBe('Pat Patel');

    host.approver.setValue('E2');
    await fixture.whenStable();
    expect(input.value).toBe('E2');
    expect(input.value).not.toContain('Patel');
  });

  it('cleans bidi controls out of labels (no visual impersonation)', async () => {
    const { type, key, tick, host } = await setup();
    await type('ev');
    host.calls[0].resolve({
      items: [{ value: 'E9', label: 'Eve \u202EleteP taP' }],
      hasMore: false,
    });
    await tick();
    expect(document.querySelector('[role=option]')!.textContent!.trim()).toBe('Eve leteP taP');
    await key('ArrowDown');
    await key('Enter');
    expect(host.approver.value).toBe('E9');
  });

  it('renders no empty listbox while loading or with no results', async () => {
    const { input, type, tick, page, host } = await setup();
    await type('zz');
    expect(document.querySelector('[role=listbox]')).toBeNull();
    expect(input.hasAttribute('aria-controls')).toBe(false);
    host.calls[0].resolve(page());
    await tick();
    expect(document.querySelector('[role=listbox]')).toBeNull();
  });
});
