import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamFormField } from '../forms/form-field';
import { IamOption } from './choice';
import { IAM_DROPDOWN_MAX_QUERY, IamDropdown } from './dropdown';

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamDropdown],
  template: `
    <iam-form-field label="Company" [required]="true">
      <iam-dropdown
        [formControl]="company"
        [options]="options()"
        [searchable]="searchable()"
        [filterLocally]="filterLocally()"
        [loading]="loading()"
        [loadError]="loadError()"
        (searchChange)="searches.push($event)"
      />
    </iam-form-field>
  `,
})
class Host {
  readonly options = signal<readonly IamOption<string>[]>([
    { value: '0100', label: 'ABC Holdings' },
    { value: '0200', label: 'Bravo Ltd', disabled: true },
    { value: '0300', label: 'Charlie GmbH' },
    { value: '0400', label: 'Delta Inc' },
  ]);
  readonly searchable = signal(false);
  readonly filterLocally = signal(true);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly searches: string[] = [];
  readonly company = new FormControl<string | null>(null, Validators.required);
}

function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  const root: HTMLElement = fixture.nativeElement;
  const combobox = root.querySelector<HTMLElement>('[role=combobox]')!;
  const key = (target: HTMLElement, k: string) => {
    target.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
    fixture.detectChanges();
  };
  const listbox = () => document.querySelector<HTMLElement>('[role=listbox]');
  const options = () => [...document.querySelectorAll<HTMLElement>('[role=option]')];
  return { fixture, root, combobox, key, listbox, options, host: fixture.componentInstance };
}

describe('iam-dropdown', () => {
  afterEach(() =>
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => c.replaceChildren()),
  );

  it('is a labelled, collapsed combobox until opened', () => {
    const { root, combobox, listbox } = setup();
    expect(combobox.getAttribute('aria-expanded')).toBe('false');
    expect(combobox.getAttribute('aria-haspopup')).toBe('listbox');
    expect(combobox.getAttribute('aria-labelledby')).toBe(root.querySelector('label')!.id);
    expect(combobox.getAttribute('tabindex')).toBe('0');
    expect(listbox()).toBeNull();
  });

  it('opens with ArrowDown, moves past disabled options and selects with Enter', () => {
    const { combobox, key, listbox, options, host } = setup();
    key(combobox, 'ArrowDown');
    expect(combobox.getAttribute('aria-expanded')).toBe('true');
    expect(combobox.getAttribute('aria-controls')).toBe(listbox()!.id);
    expect(options()).toHaveLength(4);
    expect(combobox.getAttribute('aria-activedescendant')).toBe(options()[0].id);

    key(combobox, 'ArrowDown');
    expect(combobox.getAttribute('aria-activedescendant')).toBe(options()[2].id);
    key(combobox, 'Enter');
    expect(host.company.value).toBe('0300');
    expect(combobox.getAttribute('aria-expanded')).toBe('false');
    expect(combobox.textContent).toContain('Charlie GmbH');
  });

  it('keeps string codes exactly, including leading zeros', () => {
    const { combobox, key, host } = setup();
    key(combobox, 'Enter');
    key(combobox, 'Enter');
    expect(host.company.value).toBe('0100');
  });

  it('supports Home, End and type-ahead', () => {
    const { combobox, key, options } = setup();
    key(combobox, 'ArrowDown');
    key(combobox, 'End');
    expect(combobox.getAttribute('aria-activedescendant')).toBe(options()[3].id);
    key(combobox, 'Home');
    expect(combobox.getAttribute('aria-activedescendant')).toBe(options()[0].id);
    key(combobox, 'c');
    expect(combobox.getAttribute('aria-activedescendant')).toBe(options()[2].id);
  });

  it('closes on Escape without changing the value and returns focus', () => {
    const { combobox, key, host } = setup();
    combobox.focus();
    key(combobox, 'ArrowDown');
    key(combobox, 'ArrowDown');
    key(combobox, 'Escape');
    expect(combobox.getAttribute('aria-expanded')).toBe('false');
    expect(host.company.value).toBeNull();
    expect(document.activeElement).toBe(combobox);
  });

  it('ignores clicks on disabled options and selects others by mouse', () => {
    const { combobox, options, host, fixture } = setup();
    combobox.click();
    fixture.detectChanges();
    options()[1].click();
    fixture.detectChanges();
    expect(host.company.value).toBeNull();
    options()[3].click();
    fixture.detectChanges();
    expect(host.company.value).toBe('0400');
  });

  it('filters locally and emits a debounced, capped search for server-side options', async () => {
    const { combobox, fixture, host, options } = setup();
    host.searchable.set(true);
    fixture.detectChanges();
    combobox.click();
    fixture.detectChanges();
    const search = document.querySelector<HTMLInputElement>('[role=searchbox]')!;
    expect(search.getAttribute('maxlength')).toBe(String(IAM_DROPDOWN_MAX_QUERY));
    search.value = 'gmbh';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(options().map((o) => o.textContent!.trim())).toEqual(['Charlie GmbH']);

    search.value = 'x'.repeat(500);
    search.dispatchEvent(new Event('input'));
    await new Promise((r) => setTimeout(r, 300));
    expect(host.searches.at(-1)!.length).toBe(IAM_DROPDOWN_MAX_QUERY);
  });

  it('announces loading, errors and empty results', () => {
    const { combobox, fixture, host } = setup();
    host.loading.set(true);
    fixture.detectChanges();
    combobox.click();
    fixture.detectChanges();
    expect(document.querySelector('[role=status]')!.textContent).toContain('Loading options');

    host.loading.set(false);
    host.loadError.set('Companies could not be loaded.');
    fixture.detectChanges();
    expect(document.querySelector('.iam-dropdown__panel [role=alert]')!.textContent).toContain(
      'Companies could not be loaded.',
    );

    host.loadError.set(null);
    host.options.set([]);
    fixture.detectChanges();
    expect(document.querySelector('.iam-dropdown__panel [role=status]')!.textContent).toContain(
      'No matching options',
    );
  });

  it('renders option labels as text', () => {
    const { combobox, fixture, host } = setup();
    host.options.set([{ value: 'x', label: XSS_PAYLOAD }]);
    fixture.detectChanges();
    combobox.click();
    fixture.detectChanges();
    expect(document.querySelector('.iam-dropdown__panel img')).toBeNull();
    expect(document.querySelector('[role=option]')!.textContent).toContain(XSS_PAYLOAD);
  });

  it('has no serious accessibility violations, closed or open', async () => {
    const { root, combobox, fixture } = setup();
    expect(await a11yViolations(root)).toEqual([]);
    combobox.click();
    fixture.detectChanges();
    expect(await a11yViolations(document.body)).toEqual([]);
  });
});
