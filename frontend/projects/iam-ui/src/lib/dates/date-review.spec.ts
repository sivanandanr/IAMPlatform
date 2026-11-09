import { Component, LOCALE_ID, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { a11yViolations } from '../../testing/a11y';
import { IamFormField } from '../forms/form-field';
import { IamDatePicker } from './date-picker';
import { IamDateRange, IamDateRangeValue } from './date-range';

/** Regression tests for the S1-10 independent review findings on the date controls. */

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamDatePicker, IamDateRange],
  template: `
    <iam-form-field label="Start date" controlId="start">
      <iam-date-picker [formControl]="date" [min]="min()" />
    </iam-form-field>
    <iam-date-range
      [formControl]="range"
      legend="Access period"
      [min]="rangeMin()"
      [max]="rangeMax()"
    />
  `,
})
class Host {
  readonly min = signal<string | null>('2026-01-01');
  readonly rangeMin = signal<string | null>('2026-01-01');
  readonly rangeMax = signal<string | null>('2026-12-31');
  readonly date = new FormControl<string | null>(null);
  readonly range = new FormControl<IamDateRangeValue>(
    { start: null, end: null },
    { nonNullable: true },
  );
}

async function setup(locale = 'en-GB') {
  TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: locale }] });
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
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
  const picker = root.querySelector<HTMLInputElement>('#start')!;
  const [rangeStart, rangeEnd] = root.querySelectorAll<HTMLInputElement>('iam-date-range input');
  return {
    fixture,
    root,
    type,
    key,
    picker,
    rangeStart,
    rangeEnd,
    host: fixture.componentInstance,
  };
}

describe('date controls after review', () => {
  it('range enforces its min and max on both ends and shows the error', async () => {
    const { root, type, host, rangeStart, rangeEnd } = await setup();
    await type(rangeStart, '31/12/2025');
    expect(host.range.errors?.['iamDateMin']).toBe('Dates must be on or after 1 Jan 2026.');
    expect(root.querySelector('iam-date-range')!.textContent).toContain('on or after 1 Jan 2026');
    await type(rangeStart, '01/01/2026');
    await type(rangeEnd, '01/01/2027');
    expect(host.range.errors?.['iamDateMax']).toBe('Dates must be on or before 31 Dec 2026.');
    await type(rangeEnd, '31/12/2026');
    expect(host.range.valid).toBe(true);
  });

  it('keeps its own date check when feature code replaces the validators', async () => {
    const { fixture, type, host, picker } = await setup();
    host.date.setValidators(Validators.required);
    host.date.updateValueAndValidity();
    await fixture.whenStable();
    await type(picker, '99/99/2026');
    expect(host.date.value).toBeNull();
    expect(host.date.errors?.['iamDate']).toContain('dd/mm/yyyy');

    host.date.clearValidators();
    host.date.updateValueAndValidity();
    await fixture.whenStable();
    await type(picker, '31/02/2026');
    expect(host.date.errors?.['iamDate']).toBeDefined();
  });

  it('re-validates when min changes', async () => {
    const { fixture, type, host, picker } = await setup();
    await type(picker, '01/03/2026');
    expect(host.date.valid).toBe(true);
    host.min.set('2026-06-01');
    await fixture.whenStable();
    expect(host.date.errors?.['iamDateMin']).toContain('1 Jun 2026');
    host.min.set(null);
    await fixture.whenStable();
    expect(host.date.valid).toBe(true);
  });

  it('re-validates the range when its bounds change', async () => {
    const { fixture, type, host, rangeStart } = await setup();
    await type(rangeStart, '01/03/2026');
    expect(host.range.valid).toBe(true);
    host.rangeMin.set('2026-04-01');
    await fixture.whenStable();
    expect(host.range.errors?.['iamDateMin']).toBeDefined();
  });

  it('a form reset clears text that was not a date in the range', async () => {
    const { fixture, type, host, rangeStart } = await setup();
    await type(rangeStart, '99/99/2026');
    expect(host.range.errors?.['iamDate']).toBeDefined();
    host.range.reset();
    await fixture.whenStable();
    expect(rangeStart.value).toBe('');
    expect(host.range.errors).toBeNull();
  });

  it('emits one valueChanges per edit', async () => {
    const { type, host, picker } = await setup();
    const seen: (string | null)[] = [];
    host.date.valueChanges.subscribe((v) => seen.push(v));
    await type(picker, '01/03/2026');
    expect(seen).toEqual(['2026-03-01']);
  });

  it('marks the selected day on the grid cell (APG date picker), with no axe findings', async () => {
    const { root, fixture, host } = await setup();
    host.date.setValue('2026-06-15');
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.iam-date__toggle')!.click();
    await fixture.whenStable();
    const active = document.activeElement!;
    expect(active.tagName).toBe('TD');
    expect(active.getAttribute('aria-selected')).toBe('true');
    expect(document.querySelectorAll('.iam-calendar [aria-selected=true]')).toHaveLength(1);
    expect(document.querySelector('.iam-calendar button[aria-selected]')).toBeNull();
    expect(await a11yViolations(document.body)).toEqual([]);
  });

  it('keeps calendar navigation within four-digit years', async () => {
    const { root, fixture, key, host } = await setup();
    host.min.set(null);
    host.date.setValue('1000-06-15');
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.iam-date__toggle')!.click();
    await fixture.whenStable();
    await key('PageUp', { shiftKey: true });
    await key('PageUp', { shiftKey: true });
    expect(document.activeElement!.getAttribute('aria-label')).toBe('1 January 1000');
  });
});

describe('date picker with a locale that uses other digits', () => {
  it('shows Latin digits so it can read back what it shows', async () => {
    const { fixture, type, host, picker } = await setup('ar-EG');
    host.date.setValue('2026-03-04');
    await fixture.whenStable();
    expect(picker.value).toMatch(/2026/);
    expect(picker.value).not.toMatch(/[٠-٩]/);
    await type(picker, picker.value);
    expect(host.date.value).toBe('2026-03-04');
  });
});
