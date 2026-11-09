import { Component, LOCALE_ID, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { a11yViolations } from '../../testing/a11y';
import { IamFormField } from '../forms/form-field';
import { IamDatePicker } from './date-picker';
import { IamDateRange, IamDateRangeValue } from './date-range';

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamDatePicker, IamDateRange],
  template: `
    <iam-form-field label="Start date" controlId="start">
      <iam-date-picker [formControl]="date" [min]="min()" [max]="max()" />
    </iam-form-field>
    <iam-date-range [formControl]="range" legend="Access period" [required]="true" />
  `,
})
class Host {
  readonly min = signal<string | null>('2026-01-01');
  readonly max = signal<string | null>('2026-12-31');
  readonly date = new FormControl<string | null>(null, Validators.required);
  readonly range = new FormControl<IamDateRangeValue>(
    { start: null, end: null },
    { nonNullable: true },
  );
}

async function setup() {
  TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: 'en-GB' }] });
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
  const input = root.querySelector<HTMLInputElement>('#start')!;
  const type = async (element: HTMLInputElement, text: string) => {
    element.value = text;
    element.dispatchEvent(new Event('input'));
    element.dispatchEvent(new Event('blur'));
    element.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
    await fixture.whenStable();
  };
  const key = async (k: string, init: KeyboardEventInit = {}) => {
    (document.activeElement as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: k, bubbles: true, ...init }),
    );
    await fixture.whenStable();
  };
  return { fixture, root, input, type, key, host: fixture.componentInstance };
}

const toggle = (root: HTMLElement, index = 0) =>
  root.querySelectorAll<HTMLButtonElement>('.iam-date__toggle')[index];

describe('iam-date-picker', () => {
  it('parses the locale format into an ISO date and shows it back in the locale', async () => {
    const { input, type, host } = await setup();
    expect(input.getAttribute('placeholder')).toBe('dd/mm/yyyy');
    await type(input, '1/2/2026');
    expect(host.date.value).toBe('2026-02-01');
    expect(input.value).toBe('01/02/2026');
    await type(input, '2026-03-04');
    expect(host.date.value).toBe('2026-03-04');
  });

  it('keeps the exact date written by the form, whatever the time zone', async () => {
    const { fixture, input, host } = await setup();
    host.date.setValue('2026-10-25');
    await fixture.whenStable();
    expect(input.value).toBe('25/10/2026');
    host.date.setValue('not a date');
    await fixture.whenStable();
    expect(input.value).toBe('');
  });

  it('reports unparsable text instead of silently dropping it', async () => {
    const { root, input, type, host } = await setup();
    await type(input, '31/02/2026');
    expect(host.date.value).toBeNull();
    expect(host.date.errors).toEqual(
      expect.objectContaining({ iamDate: expect.stringContaining('dd/mm/yyyy') }),
    );
    expect(root.textContent).toContain('Enter a real date as dd/mm/yyyy.');
    expect(root.textContent).not.toContain('Start date is required.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.value).toBe('31/02/2026');
  });

  it('enforces min and max (inclusive)', async () => {
    const { root, input, type, host } = await setup();
    await type(input, '31/12/2025');
    expect(host.date.errors?.['iamDateMin']).toContain('1 Jan 2026');
    await type(input, '01/01/2026');
    expect(host.date.valid).toBe(true);
    await type(input, '01/01/2027');
    expect(root.textContent).toContain('The date must be on or before 31 Dec 2026.');
  });

  it('opens a labelled calendar dialog focused on the selected date and selects with the keyboard', async () => {
    const { root, input, type, key, host, fixture } = await setup();
    await type(input, '15/06/2026');
    toggle(root).click();
    await fixture.whenStable();
    const dialog = document.querySelector<HTMLElement>('.iam-calendar')!;
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-label')).toContain('June 2026');
    expect(document.activeElement!.getAttribute('aria-label')).toBe('15 June 2026');
    expect(document.activeElement!.getAttribute('aria-selected')).toBe('true');

    await key('ArrowRight');
    await key('ArrowDown');
    expect(document.activeElement!.getAttribute('aria-label')).toBe('23 June 2026');
    await key('PageDown');
    expect(document.activeElement!.getAttribute('aria-label')).toBe('23 July 2026');
    await key('Home');
    expect(document.activeElement!.getAttribute('aria-label')).toBe('20 July 2026');
    await key('End');
    expect(document.activeElement!.getAttribute('aria-label')).toBe('26 July 2026');
    await key('PageUp', { shiftKey: true });
    expect(document.activeElement!.getAttribute('aria-label')).toBe('26 July 2025');
    await key('PageDown', { shiftKey: true });
    await key('Enter');
    expect(host.date.value).toBe('2026-07-26');
    expect(document.querySelector('.iam-calendar')).toBeNull();
    expect(document.activeElement).toBe(input);
  });

  it('does not select dates outside min/max and closes on Escape returning focus to the button', async () => {
    const { root, key, host, fixture } = await setup();
    host.date.setValue('2026-01-01');
    await fixture.whenStable();
    toggle(root).click();
    await fixture.whenStable();
    await key('ArrowLeft');
    expect(document.activeElement!.getAttribute('aria-disabled')).toBe('true');
    await key('Enter');
    expect(host.date.value).toBe('2026-01-01');
    await key('Escape');
    expect(document.querySelector('.iam-calendar')).toBeNull();
    expect(document.activeElement).toBe(toggle(root));
  });

  it('marks today and has no accessibility violations with the calendar open', async () => {
    const { root, fixture } = await setup();
    expect(await a11yViolations(root)).toEqual([]);
    toggle(root).click();
    await fixture.whenStable();
    expect(document.querySelector('[aria-current=date]')).not.toBeNull();
    expect(await a11yViolations(document.body)).toEqual([]);
  });
});

describe('iam-date-range', () => {
  const rangeInputs = (root: HTMLElement) =>
    root.querySelectorAll<HTMLInputElement>('iam-date-range input');

  it('labels both dates and returns an inclusive ISO range', async () => {
    const { root, type, host } = await setup();
    const [start, end] = rangeInputs(root);
    expect(root.querySelector(`label[for="${start.id}"]`)!.textContent).toContain('Start date');
    expect(root.querySelector(`label[for="${end.id}"]`)!.textContent).toContain('End date');
    await type(start, '01/03/2026');
    await type(end, '01/03/2026');
    expect(host.range.value).toEqual({ start: '2026-03-01', end: '2026-03-01' });
    expect(host.range.valid).toBe(true);
  });

  it('rejects an end before the start and a half-filled required range', async () => {
    const { root, type, host } = await setup();
    const [start, end] = rangeInputs(root);
    await type(start, '10/03/2026');
    expect(host.range.errors?.['iamDateRangeRequired']).toContain('both a start and an end');
    await type(end, '09/03/2026');
    expect(host.range.errors?.['iamDateRange']).toBe(
      'The end date must be on or after the start date.',
    );
    expect(root.querySelector('iam-date-range')!.textContent).toContain(
      'The end date must be on or after the start date.',
    );
    expect(root.querySelector('iam-date-range fieldset')!.getAttribute('aria-invalid')).toBe(
      'true',
    );
  });

  it('reports unparsable text in either date', async () => {
    const { root, type, host } = await setup();
    await type(rangeInputs(root)[1], '99/99/2026');
    expect(host.range.errors?.['iamDate']).toBeDefined();
  });
});
