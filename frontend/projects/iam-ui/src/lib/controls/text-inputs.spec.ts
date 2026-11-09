import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamFormField } from '../forms/form-field';
import { IamTextarea } from './textarea';
import { IamTextbox } from './textbox';

@Component({
  imports: [IamFormField, IamTextbox, IamTextarea, ReactiveFormsModule],
  template: `
    <iam-form-field
      [label]="label()"
      hint="As shown to approvers"
      [required]="true"
      [serverErrors]="serverErrors()"
    >
      <iam-textbox [formControl]="name" [maxLength]="20" prefix="ID" />
    </iam-form-field>
    <iam-form-field label="Justification">
      <iam-textarea [formControl]="reason" [maxLength]="10" />
    </iam-form-field>
  `,
})
class Host {
  readonly label = signal('Display name');
  readonly serverErrors = signal<readonly string[]>([]);
  readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(20)],
  });
  readonly reason = new FormControl('', { nonNullable: true });
}

function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  const root: HTMLElement = fixture.nativeElement;
  const input = root.querySelector<HTMLInputElement>('iam-textbox input')!;
  const textarea = root.querySelector<HTMLTextAreaElement>('iam-textarea textarea')!;
  return { fixture, root, input, textarea, host: fixture.componentInstance };
}

function type(element: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

describe('iam-textbox in iam-form-field', () => {
  it('labels the input and describes it with the hint', () => {
    const { root, input } = setup();
    const label = root.querySelector('label')!;
    expect(label.getAttribute('for')).toBe(input.id);
    expect(label.textContent).toContain('Display name');
    expect(label.textContent).toContain('(required)');
    const hintId = input.getAttribute('aria-describedby')!;
    expect(root.querySelector(`#${hintId}`)!.textContent).toContain('As shown to approvers');
    expect(input.getAttribute('aria-required')).toBe('true');
    expect(input.getAttribute('maxlength')).toBe('20');
  });

  it('writes and reads values through the form control', () => {
    const { fixture, input, host } = setup();
    host.name.setValue('Ada');
    fixture.detectChanges();
    expect(input.value).toBe('Ada');

    type(input, 'Grace');
    expect(host.name.value).toBe('Grace');
  });

  it('shows client validation only after the user leaves the field', () => {
    const { fixture, root, input } = setup();
    expect(root.textContent).not.toContain('Display name is required.');
    expect(input.getAttribute('aria-invalid')).toBeNull();

    input.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
    expect(root.textContent).toContain('Display name is required.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toContain(
      root.querySelector('.iam-form-field__errors')!.id,
    );
  });

  it('trims surrounding spaces on blur but keeps leading zeros and inner text', () => {
    const { fixture, input, host } = setup();
    type(input, '  0042 A  ');
    input.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
    expect(host.name.value).toBe('0042 A');
  });

  it('shows server errors immediately and marks the input invalid', () => {
    const { fixture, root, input, host } = setup();
    host.serverErrors.set(['The name is already in use.']);
    fixture.detectChanges();
    expect(root.textContent).toContain('The name is already in use.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });

  it('follows the form control disabled state', () => {
    const { fixture, input, host } = setup();
    host.name.disable();
    fixture.detectChanges();
    expect(input.disabled).toBe(true);
  });

  it('renders labels and server messages as text, never HTML', () => {
    const { fixture, root, host } = setup();
    host.label.set(XSS_PAYLOAD);
    host.serverErrors.set([XSS_PAYLOAD]);
    fixture.detectChanges();
    expect(root.querySelector('img')).toBeNull();
    expect(root.textContent).toContain(XSS_PAYLOAD);
    expect((globalThis as Record<string, unknown>)['__iamXss']).toBeUndefined();
  });

  it('has no serious accessibility violations, valid or invalid', async () => {
    const { fixture, root, input, host } = setup();
    expect(await a11yViolations(root)).toEqual([]);
    input.dispatchEvent(new Event('blur'));
    host.serverErrors.set(['Taken.']);
    fixture.detectChanges();
    expect(await a11yViolations(root)).toEqual([]);
  });
});

describe('iam-textarea', () => {
  it('counts characters, bounds input and links the counter', () => {
    const { fixture, root, textarea, host } = setup();
    expect(textarea.getAttribute('maxlength')).toBe('10');
    type(textarea, 'abcdefghi');
    fixture.detectChanges();
    expect(host.reason.value).toBe('abcdefghi');
    const counter = root.querySelector('.iam-textarea__counter')!;
    expect(counter.textContent).toContain('9 / 10');
    expect(counter.classList).toContain('iam-textarea__counter--near');
    expect(textarea.getAttribute('aria-describedby')).toContain(counter.id);
  });

  it('labels the textarea from its form field', () => {
    const { root, textarea } = setup();
    const label = [...root.querySelectorAll('label')].find((l) =>
      l.textContent?.includes('Justification'),
    )!;
    expect(label.getAttribute('for')).toBe(textarea.id);
  });
});

describe('TestBed setup', () => {
  beforeEach(() => TestBed.configureTestingModule({}));
  it('creates the host', () =>
    expect(TestBed.createComponent(Host).componentInstance).toBeTruthy());
});
