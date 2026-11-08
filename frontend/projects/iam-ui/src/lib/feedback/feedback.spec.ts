import { Component, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamFieldError, IamToastOutlet, IamToastService, IamValidationSummary } from './feedback';
import { fieldErrorsFor, readProblemDetails } from './problem-details';

describe('readProblemDetails', () => {
  it('reads an ASP.NET Core ValidationProblemDetails', () => {
    const problem = readProblemDetails({
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { DisplayName: ['The name is already in use.'], 'Items[0].Code': ['Unknown code.'] },
      traceId: '00-abc',
    });
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.status).toBe(400);
    expect(fieldErrorsFor(problem, 'displayName')).toEqual(['The name is already in use.']);
    expect(problem.fieldErrors['Items[0].Code']).toEqual(['Unknown code.']);
  });

  it.each([null, undefined, 'text', 42, [], ['x']])(
    'falls back to a generic message for %p',
    (body) => {
      const problem = readProblemDetails(body);
      expect(problem.title).toBe('The request could not be completed.');
      expect(problem.fieldErrors).toEqual({});
    },
  );

  it('bounds sizes, drops control characters and ignores malformed entries', () => {
    const errors: Record<string, unknown> = {
      ok: ['fine'],
      '<script>': ['bad name'],
      nums: [1, 2],
      notArray: 'x',
    };
    for (let i = 0; i < 80; i++) {
      errors[`f${i}`] = ['m'];
    }

    const problem = readProblemDetails({
      title: 'T'.repeat(5000),
      detail: 'line1\nline2\u0007',
      status: 99999,
      errors: { ...errors, long: Array.from({ length: 30 }, () => 'y'.repeat(2000)) },
    });
    expect(problem.title.length).toBeLessThanOrEqual(200);
    expect(problem.detail).toBe('line1 line2');
    expect(problem.status).toBeNull();
    expect(problem.fieldErrors['<script>']).toBeUndefined();
    expect(problem.fieldErrors['nums']).toBeUndefined();
    expect(problem.fieldErrors['notArray']).toBeUndefined();
    expect(Object.keys(problem.fieldErrors).length).toBeLessThanOrEqual(50);
    expect(fieldErrorsFor(problem, 'ok')).toEqual(['fine']);
  });
});

@Component({
  imports: [IamValidationSummary, IamToastOutlet],
  template: `
    <input id="displayName" aria-label="Display name" />
    <iam-validation-summary [errors]="errors()" [general]="general()" />
    <iam-toast-outlet />
  `,
})
class Host {
  readonly errors = signal<readonly IamFieldError[]>([]);
  readonly general = signal<readonly string[]>([]);
  readonly summary = viewChild.required(IamValidationSummary);
}

function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  return { fixture, root: fixture.nativeElement as HTMLElement, host: fixture.componentInstance };
}

describe('iam-validation-summary', () => {
  it('is hidden without errors', () => {
    const { root } = setup();
    expect(root.querySelector('iam-validation-summary')!.hasAttribute('hidden')).toBe(true);
  });

  it('lists problems, takes focus and moves focus to a field', async () => {
    const { fixture, root, host } = setup();
    host.errors.set([
      { label: 'Display name', message: 'Already in use.', fieldId: 'displayName' },
    ]);
    host.general.set(['The request could not be completed.']);
    fixture.detectChanges();
    const summary = root.querySelector<HTMLElement>('.iam-summary')!;
    expect(summary.getAttribute('role')).toBe('alert');
    expect(summary.textContent).toContain('Display name: Already in use.');
    host.summary().focus();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(summary);
    summary.querySelector<HTMLButtonElement>('.iam-summary__link')!.click();
    expect(document.activeElement).toBe(root.querySelector('#displayName'));
  });

  it('does not offer focus links for unsafe ids and renders text only', () => {
    const { fixture, root, host } = setup();
    host.errors.set([{ label: XSS_PAYLOAD, message: XSS_PAYLOAD, fieldId: 'x"] , body' }]);
    fixture.detectChanges();
    expect(root.querySelector('.iam-summary__link')).toBeNull();
    expect(root.querySelector('.iam-summary img')).toBeNull();
    expect(root.querySelector('.iam-summary')!.textContent).toContain(XSS_PAYLOAD);
  });

  it('has no serious accessibility violations', async () => {
    const { fixture, root, host } = setup();
    host.errors.set([{ label: 'Display name', message: 'Required.', fieldId: 'displayName' }]);
    fixture.detectChanges();
    expect(await a11yViolations(root)).toEqual([]);
  });
});

describe('IamToastService and iam-toast-outlet', () => {
  afterEach(() => vi.useRealTimers());

  it('announces info politely and errors assertively', () => {
    const { fixture, root } = setup();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('Saved.', 'success');
    toasts.show('Could not save.', 'error');
    fixture.detectChanges();
    expect(root.querySelector('[aria-live=polite]')!.textContent).toContain('Saved.');
    expect(root.querySelector('[aria-live=assertive]')!.textContent).toContain('Could not save.');
  });

  it('dismisses info automatically but keeps errors until dismissed', () => {
    vi.useFakeTimers();
    const { fixture, root } = setup();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('Saved.', 'success');
    const errorId = toasts.show('Could not save.', 'error');
    vi.advanceTimersByTime(7000);
    fixture.detectChanges();
    expect(root.textContent).not.toContain('Saved.');
    expect(root.textContent).toContain('Could not save.');
    toasts.dismiss(errorId);
    fixture.detectChanges();
    expect(root.textContent).not.toContain('Could not save.');
  });

  it('keeps at most five toasts and renders text only', () => {
    const { fixture, root } = setup();
    const toasts = TestBed.inject(IamToastService);
    for (let i = 0; i < 8; i++) {
      toasts.show(`${XSS_PAYLOAD} ${i}`, 'warning');
    }

    fixture.detectChanges();
    expect(root.querySelectorAll('.iam-toast')).toHaveLength(5);
    expect(root.querySelector('iam-toast-outlet img')).toBeNull();
  });
});
