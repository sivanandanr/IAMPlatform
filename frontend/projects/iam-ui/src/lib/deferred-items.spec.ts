import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { IamCheckbox, IamOption, IamRadioGroup } from './controls/choice';
import { IamDropdown } from './controls/dropdown';
import { IamTextbox } from './controls/textbox';
import { IamToastOutlet, IamToastService, IamValidationSummary } from './feedback/feedback';
import { IamDialog, IamDialogService } from './overlays/dialog';
import { IamTooltip } from './overlays/tooltip';

/** Regression tests for the items deferred from the S1-09 review and done in S1-10. */

@Component({
  imports: [
    ReactiveFormsModule,
    IamCheckbox,
    IamRadioGroup,
    IamDropdown,
    IamTextbox,
    IamValidationSummary,
  ],
  template: `
    <iam-checkbox
      [formControl]="accept"
      label="I accept the terms"
      [serverErrors]="checkboxServer()"
    />
    <iam-radio-group [formControl]="level" legend="Access level" [options]="levels" />
    <iam-dropdown
      [formControl]="company"
      [options]="companies"
      [clearable]="true"
      ariaLabel="Company"
    />
    <iam-textbox [formControl]="name" [maxLength]="20" [disabled]="true" ariaLabel="Name" />
    <iam-textbox [maxLength]="20" [disabled]="standaloneDisabled()" ariaLabel="Standalone" />
    <iam-validation-summary
      [errors]="[{ label: 'Name', message: 'Required.' }]"
      [headingLevel]="3"
    />
  `,
})
class Host {
  readonly checkboxServer = signal<readonly string[]>([]);
  readonly standaloneDisabled = signal(true);
  readonly levels: IamOption<string>[] = [
    { value: 'read', label: 'Read' },
    { value: 'write', label: 'Write' },
  ];
  readonly companies: IamOption<string>[] = [{ value: '0100', label: 'ABC Holdings' }];
  readonly accept = new FormControl(false, {
    nonNullable: true,
    validators: [Validators.requiredTrue],
  });
  readonly level = new FormControl<string | null>(null, Validators.required);
  readonly company = new FormControl<string | null>('0100');
  readonly name = new FormControl('');
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, host: fixture.componentInstance };
}

describe('S1-09 follow-ups', () => {
  it('lets the validation summary use a heading level that fits the page', async () => {
    const { root } = await setup();
    expect(root.querySelector('iam-validation-summary h3')!.textContent).toContain(
      'Please correct',
    );
    expect(root.querySelector('iam-validation-summary h2')).toBeNull();
  });

  it('shows checkbox errors without a form field, and server errors too', async () => {
    const { fixture, root, host } = await setup();
    const box = root.querySelector<HTMLInputElement>('iam-checkbox input')!;
    expect(root.textContent).not.toContain('must be checked');
    host.accept.markAsTouched();
    await fixture.whenStable();
    expect(root.querySelector('iam-checkbox')!.textContent).toContain(
      'I accept the terms must be checked.',
    );
    expect(box.getAttribute('aria-invalid')).toBe('true');
    const errorsId = root.querySelector('iam-checkbox .iam-choice__errors')!.id;
    expect(box.getAttribute('aria-describedby')).toContain(errorsId);

    box.click();
    host.checkboxServer.set(['Terms version is out of date.']);
    await fixture.whenStable();
    expect(root.querySelector('iam-checkbox')!.textContent).not.toContain('must be checked');
    expect(root.querySelector('iam-checkbox')!.textContent).toContain(
      'Terms version is out of date.',
    );
  });

  it('never shows a stale radio group error after the value changes', async () => {
    const { fixture, root, host } = await setup();
    host.level.markAsTouched();
    await fixture.whenStable();
    expect(root.querySelector('iam-radio-group')!.textContent).toContain(
      'Access level is required.',
    );
    root.querySelector<HTMLInputElement>('iam-radio-group input')!.click();
    await fixture.whenStable();
    expect(host.level.value).toBe('read');
    expect(root.querySelector('iam-radio-group')!.textContent).not.toContain('is required');
  });

  it('clears the dropdown selection and returns focus to it', async () => {
    const { fixture, root, host } = await setup();
    const clear = root.querySelector<HTMLButtonElement>('.iam-dropdown__clear')!;
    expect(clear.getAttribute('aria-label')).toBe('Clear selection');
    clear.click();
    await fixture.whenStable();
    expect(host.company.value).toBeNull();
    expect(root.querySelector('.iam-dropdown__clear')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('iam-dropdown [role=combobox]'));
  });

  it('uses the form as the single source of disabled state', async () => {
    const { fixture, root, host } = await setup();
    const [bound, standalone] = [...root.querySelectorAll<HTMLInputElement>('iam-textbox input')];
    // [disabled]="true" is ignored when bound to a form; the form control is enabled.
    expect(bound.disabled).toBe(false);
    host.name.disable();
    await fixture.whenStable();
    expect(bound.disabled).toBe(true);
    // Standalone, the input is the source.
    expect(standalone.disabled).toBe(true);
    host.standaloneDisabled.set(false);
    await fixture.whenStable();
    expect(standalone.disabled).toBe(false);
  });
});

@Component({
  imports: [IamDialog],
  template: `<iam-dialog heading="Details" [headingLevel]="3">Body</iam-dialog>`,
})
class LevelDialog {}

describe('toasts and dialog headings', () => {
  afterEach(() => vi.useRealTimers());

  it('pauses auto-dismiss while hovered or focused, then resumes with the time left', async () => {
    vi.useFakeTimers();
    const fixture = TestBed.createComponent(IamToastOutlet);
    fixture.detectChanges();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('Saved.', 'success', 1000);
    vi.advanceTimersByTime(600);
    fixture.nativeElement.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(5000);
    expect(toasts.toasts().map((t) => t.message)).toEqual(['Saved.']);
    fixture.nativeElement.dispatchEvent(new MouseEvent('mouseleave'));
    vi.advanceTimersByTime(300);
    expect(toasts.toasts()).toHaveLength(1);
    vi.advanceTimersByTime(200);
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('renders the dialog title at the requested level and still names the dialog', async () => {
    const fixture = TestBed.createComponent(IamToastOutlet);
    fixture.autoDetectChanges();
    TestBed.inject(IamDialogService).open(LevelDialog);
    await fixture.whenStable();
    const dialog = document.querySelector('[role=dialog]')!;
    const title = dialog.querySelector('h3')!;
    expect(title.textContent).toContain('Details');
    expect(dialog.getAttribute('aria-labelledby')).toBe(title.id);
  });
});

describe('toast pause after review', () => {
  afterEach(() => vi.useRealTimers());

  it('keeps a focused toast while the pointer passes over and leaves', async () => {
    vi.useFakeTimers();
    const fixture = TestBed.createComponent(IamToastOutlet);
    fixture.detectChanges();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('Saved.', 'success', 1000);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    host.dispatchEvent(new FocusEvent('focusin'));
    host.dispatchEvent(new MouseEvent('mouseenter'));
    host.dispatchEvent(new MouseEvent('mouseleave'));
    vi.advanceTimersByTime(5000);
    expect(toasts.toasts()).toHaveLength(1);

    host.dispatchEvent(new FocusEvent('focusout', { relatedTarget: null }));
    vi.advanceTimersByTime(1000);
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('stays paused while focus moves between toasts', async () => {
    vi.useFakeTimers();
    const fixture = TestBed.createComponent(IamToastOutlet);
    fixture.detectChanges();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('One.', 'success', 1000);
    toasts.show('Two.', 'success', 1000);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const [first, second] = host.querySelectorAll('button');
    host.dispatchEvent(new FocusEvent('focusin'));
    host.dispatchEvent(new FocusEvent('focusout', { relatedTarget: second }));
    vi.advanceTimersByTime(5000);
    expect(toasts.toasts()).toHaveLength(2);
    void first;
  });

  it('moves focus to the next toast when the focused one is dismissed, and resumes when none are left', async () => {
    const fixture = TestBed.createComponent(IamToastOutlet);
    fixture.autoDetectChanges();
    const toasts = TestBed.inject(IamToastService);
    toasts.show('One.', 'success', 50);
    toasts.show('Two.', 'error');
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const dismissButtons = () => [...host.querySelectorAll<HTMLButtonElement>('button')];
    dismissButtons()[1].focus();
    host.dispatchEvent(new FocusEvent('focusin'));
    dismissButtons()[1].click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(dismissButtons()[0]);
    // Still paused: the remaining 50 ms toast survives while focused.
    await new Promise((r) => setTimeout(r, 120));
    expect(toasts.toasts().map((t) => t.message)).toEqual(['One.']);

    dismissButtons()[0].click();
    await fixture.whenStable();
    toasts.show('Later.', 'success', 30);
    await new Promise((r) => setTimeout(r, 80));
    expect(toasts.toasts()).toHaveLength(0);
  });
});

@Component({
  imports: [IamTooltip],
  template: `<button type="button" id="tip" iamTooltip="Removed at the end of the period">
    Info
  </button>`,
})
class TooltipHost {}

describe('tooltip after review', () => {
  it('stays open when the pointer leaves while the button has keyboard focus', async () => {
    const fixture = TestBed.createComponent(TooltipHost);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('#tip')!;
    button.focus();
    button.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    button.dispatchEvent(new MouseEvent('mouseleave'));
    await new Promise((r) => setTimeout(r, 400));
    expect(document.querySelector('[role=tooltip]')).not.toBeNull();
  });
});
