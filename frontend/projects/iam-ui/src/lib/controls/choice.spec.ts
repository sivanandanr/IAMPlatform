import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamButton, IamIconButton } from './button';
import { IamCheckbox, IamOption, IamRadioGroup, IamToggle } from './choice';

@Component({
  imports: [ReactiveFormsModule, IamCheckbox, IamToggle, IamRadioGroup, IamButton, IamIconButton],
  template: `
    <iam-checkbox
      [formControl]="accept"
      [label]="checkboxLabel()"
      hint="Required before submitting"
    />
    <iam-toggle [formControl]="notify" label="Email notifications" />
    <iam-radio-group
      [formControl]="level"
      legend="Access level"
      [options]="levels"
      [required]="true"
      [serverErrors]="radioErrors()"
    />
    <button iamButton variant="primary" [loading]="saving()" (click)="clicks.set(clicks() + 1)">
      Save
    </button>
    <button iamIconButton label="Close panel"><span aria-hidden="true">x</span></button>
  `,
})
class Host {
  readonly checkboxLabel = signal('I accept the terms');
  readonly radioErrors = signal<readonly string[]>([]);
  readonly saving = signal(false);
  readonly clicks = signal(0);
  readonly accept = new FormControl(false, { nonNullable: true });
  readonly notify = new FormControl(false, { nonNullable: true });
  readonly level = new FormControl<string | null>(null, Validators.required);
  readonly levels: IamOption<string>[] = [
    { value: 'read', label: 'Read' },
    { value: 'write', label: 'Write' },
    { value: 'admin', label: 'Admin', disabled: true },
  ];
}

function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  return { fixture, root: fixture.nativeElement as HTMLElement, host: fixture.componentInstance };
}

describe('iam-checkbox', () => {
  it('is a labelled native checkbox bound to a boolean', () => {
    const { fixture, root, host } = setup();
    const box = root.querySelector<HTMLInputElement>('iam-checkbox input')!;
    expect(box.type).toBe('checkbox');
    expect(box.closest('label')!.textContent).toContain('I accept the terms');
    box.click();
    fixture.detectChanges();
    expect(host.accept.value).toBe(true);
    host.accept.setValue(false);
    fixture.detectChanges();
    expect(box.checked).toBe(false);
  });
});

describe('iam-toggle', () => {
  it('is a switch whose aria-checked follows the value', () => {
    const { fixture, root, host } = setup();
    const toggle = root.querySelector<HTMLButtonElement>('iam-toggle button')!;
    expect(toggle.getAttribute('role')).toBe('switch');
    expect(toggle.getAttribute('aria-checked')).toBe('false');
    toggle.click();
    fixture.detectChanges();
    expect(host.notify.value).toBe(true);
    expect(toggle.getAttribute('aria-checked')).toBe('true');
    host.notify.disable();
    fixture.detectChanges();
    expect(toggle.disabled).toBe(true);
  });
});

describe('iam-radio-group', () => {
  it('groups native radios under a legend and selects by value', () => {
    const { fixture, root, host } = setup();
    const fieldset = root.querySelector('iam-radio-group fieldset')!;
    expect(fieldset.querySelector('legend')!.textContent).toContain('Access level');
    const radios = root.querySelectorAll<HTMLInputElement>('iam-radio-group input[type=radio]');
    expect(radios).toHaveLength(3);
    expect(new Set([...radios].map((r) => r.name)).size).toBe(1);
    expect(radios[2].disabled).toBe(true);
    radios[1].click();
    fixture.detectChanges();
    expect(host.level.value).toBe('write');
    host.level.setValue('read');
    fixture.detectChanges();
    expect(radios[0].checked).toBe(true);
  });

  it('reports required and server errors on the group', () => {
    const { fixture, root, host } = setup();
    root.querySelector<HTMLInputElement>('iam-radio-group input')!.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
    const fieldset = root.querySelector('iam-radio-group fieldset')!;
    expect(root.textContent).toContain('Access level is required.');
    expect(fieldset.getAttribute('aria-invalid')).toBe('true');
    host.radioErrors.set(['Admin access needs a separate request.']);
    fixture.detectChanges();
    expect(root.textContent).toContain('Admin access needs a separate request.');
  });
});

describe('iam-button and iam-icon-button', () => {
  it('defaults to type=button and ignores clicks while loading', () => {
    const { fixture, root, host } = setup();
    const button = root.querySelector<HTMLButtonElement>('button[iamButton]')!;
    expect(button.getAttribute('type')).toBe('button');
    button.click();
    expect(host.clicks()).toBe(1);
    host.saving.set(true);
    fixture.detectChanges();
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.getAttribute('aria-busy')).toBe('true');
    button.click();
    expect(host.clicks()).toBe(1);
  });

  it('gives icon buttons an accessible name', () => {
    const { root } = setup();
    expect(root.querySelector('button[iamIconButton]')!.getAttribute('aria-label')).toBe(
      'Close panel',
    );
  });
});

describe('choice controls', () => {
  it('render labels as text', () => {
    const { fixture, root, host } = setup();
    host.checkboxLabel.set(XSS_PAYLOAD);
    fixture.detectChanges();
    expect(root.querySelector('img')).toBeNull();
    expect(root.textContent).toContain(XSS_PAYLOAD);
  });

  it('have no serious accessibility violations', async () => {
    const { root } = setup();
    expect(await a11yViolations(root)).toEqual([]);
  });
});
