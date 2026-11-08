import { Component, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { IamCheckbox, IamOption, IamRadioGroup } from './controls/choice';
import { IamDropdown } from './controls/dropdown';
import { IamTextbox } from './controls/textbox';
import { IamToastOutlet, IamToastService, IamValidationSummary } from './feedback/feedback';
import { readProblemDetails } from './feedback/problem-details';
import { IamFormField } from './forms/form-field';
import { IamConfirmService, IamDialog, IamDialogService } from './overlays/dialog';

/** Regression tests for the S1-09 independent review findings. */

const clearOverlays = () =>
  document.querySelectorAll('.cdk-overlay-container').forEach((c) => c.replaceChildren());

@Component({
  imports: [
    ReactiveFormsModule,
    IamFormField,
    IamTextbox,
    IamRadioGroup,
    IamCheckbox,
    IamValidationSummary,
  ],
  template: `
    <form [formGroup]="form">
      <iam-form-field label="Display name" [controlId]="nameId()">
        <iam-textbox formControlName="name" [maxLength]="50" />
      </iam-form-field>
      <iam-form-field label="Nickname">
        <iam-textbox [formControl]="swappable()" [maxLength]="50" />
      </iam-form-field>
      <iam-radio-group
        formControlName="level"
        legend="Access level"
        [options]="levels"
        controlId="level-group"
      />
      <iam-checkbox
        formControlName="accept"
        label="I agree"
        hint="Read the terms first"
        controlId="accept-box"
      />
    </form>
    <iam-validation-summary [errors]="[]" />
  `,
})
class FormHost {
  readonly nameId = signal<string | null>('display-name');
  readonly levels: IamOption<string>[] = [{ value: 'read', label: 'Read' }];
  readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    level: new FormControl<string | null>(null, Validators.required),
    accept: new FormControl(false, { nonNullable: true }),
  });
  readonly first = new FormControl('', { nonNullable: true });
  readonly second = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly swappable = signal(this.first);
}

function formSetup() {
  const fixture = TestBed.createComponent(FormHost);
  fixture.detectChanges();
  fixture.detectChanges();
  return { fixture, root: fixture.nativeElement as HTMLElement, host: fixture.componentInstance };
}

describe('form plumbing (review fixes)', () => {
  it('derives the required marker from Validators.required when not set explicitly', () => {
    const { root } = formSetup();
    const labels = [...root.querySelectorAll('iam-form-field label')];
    expect(labels[0].textContent).toContain('(required)');
    expect(labels[1].textContent).not.toContain('(required)');
    expect(root.querySelector('#display-name')!.getAttribute('aria-required')).toBe('true');
    expect(root.querySelector('iam-radio-group legend')!.textContent).toContain('(required)');
  });

  it('uses a caller-chosen id when it is safe and ignores unsafe ones', () => {
    const { fixture, root, host } = formSetup();
    const label = root.querySelector('iam-form-field label')!;
    expect(root.querySelector('#display-name')!.tagName).toBe('INPUT');
    expect(label.getAttribute('for')).toBe('display-name');
    expect(root.querySelector('#level-group')!.tagName).toBe('FIELDSET');
    expect(root.querySelector('#accept-box')!.getAttribute('type')).toBe('checkbox');
    expect(root.querySelector('iam-radio-group')!.hasAttribute('id')).toBe(false);

    host.nameId.set('x" onfocus="alert(1)');
    fixture.detectChanges();
    const input = root.querySelector('iam-form-field iam-textbox input')!;
    expect(input.id).toMatch(/^iam-input-\d+$/);
    expect(label.getAttribute('for')).toBe(input.id);
  });

  it('shows errors after markAllAsTouched without a blur, and clears them on reset', async () => {
    const { fixture, root, host } = formSetup();
    host.form.markAllAsTouched();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent).toContain('Display name is required.');
    expect(root.textContent).toContain('Access level is required.');

    host.form.reset();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent).not.toContain('is required.');
    expect(root.querySelector<HTMLInputElement>('#display-name')!.value).toBe('');
  });

  it('follows a swapped [formControl] instance', async () => {
    const { fixture, root, host } = formSetup();
    host.swappable.set(host.second);
    fixture.detectChanges();
    host.second.markAsTouched();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.textContent).toContain('Nickname is required.');
  });

  it('keeps the checkbox hint and the field ids together in aria-describedby', () => {
    const { root } = formSetup();
    const box = root.querySelector('#accept-box')!;
    const hint = root.querySelector('iam-checkbox .iam-choice__hint')!;
    expect(box.getAttribute('aria-describedby')).toContain(hint.id);
  });

  it('really hides an empty validation summary', () => {
    const { root } = formSetup();
    const summary = root.querySelector<HTMLElement>('iam-validation-summary')!;
    expect(summary.hasAttribute('hidden')).toBe(true);
    // No content at all, so no empty alert can show even if CSS fails to hide the host.
    expect(summary.querySelector('.iam-summary')).toBeNull();
    expect(summary.textContent!.trim()).toBe('');
  });
});

describe('ProblemDetails and toasts text (review fixes)', () => {
  it('removes bidi overrides and zero-width characters and has no prototype', () => {
    const problem = readProblemDetails({
      title: 'Approve‮evil‬ now​',
      errors: { constructor: ['x'], name: ['a⁦b⁩'] },
    });
    expect(problem.title).toBe('Approveevil now');
    expect(problem.fieldErrors['name']).toEqual(['ab']);
    expect(Object.getPrototypeOf(problem.fieldErrors)).toBeNull();
    expect(problem.fieldErrors['constructor']).toEqual(['x']);
  });

  it('cleans toast text, keeps surrogate pairs whole and rejects unknown kinds', () => {
    const toasts = TestBed.inject(IamToastService);
    toasts.show('line\u0007one‮', 'bogus' as never);
    const [toast] = toasts.toasts();
    expect(toast.message).toBe('line one');
    expect(toast.kind).toBe('info');
    toasts.show('\u{1F600}'.repeat(400), 'warning');
    const long = toasts.toasts().at(-1)!.message;
    expect(Array.from(long).length).toBe(300);
    // No lone high surrogate (a pair cut in half).
    expect(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/.test(long)).toBe(false);
    expect(long.endsWith('…')).toBe(true);
  });

  it('keeps errors when the toast cap is reached', () => {
    const fixture = TestBed.createComponent(IamToastOutlet);
    const toasts = TestBed.inject(IamToastService);
    toasts.show('Could not save.', 'error');
    for (let i = 0; i < 6; i++) {
      toasts.show(`Saved ${i}.`, 'success');
    }

    fixture.detectChanges();
    expect(toasts.toasts()).toHaveLength(5);
    expect(toasts.toasts().map((t) => t.message)).toContain('Could not save.');
  });
});

@Component({
  imports: [ReactiveFormsModule, IamFormField, IamDropdown],
  template: `
    <iam-form-field label="Company">
      <iam-dropdown
        [formControl]="company"
        [options]="options()"
        [searchable]="searchable()"
        (searchChange)="searches.push($event)"
      />
    </iam-form-field>
    <button type="button" id="outside">Outside</button>
  `,
})
class DropdownHost {
  readonly options = signal<readonly IamOption<string>[]>(
    Array.from({ length: 40 }, (_, i) => ({
      value: String(i).padStart(4, '0'),
      label: `Company ${i}`,
    })),
  );
  readonly searchable = signal(false);
  readonly searches: string[] = [];
  readonly company = new FormControl<string | null>(null);
}

describe('iam-dropdown (review fixes)', () => {
  afterEach(clearOverlays);

  async function setup(searchable: boolean) {
    const fixture = TestBed.createComponent(DropdownHost);
    fixture.componentInstance.searchable.set(searchable);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const combobox = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[role=combobox]',
    )!;
    return { fixture, combobox, host: fixture.componentInstance };
  }

  it('moves focus into the search box once the panel has rendered', async () => {
    const { fixture, combobox } = await setup(true);
    combobox.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(document.querySelector('[role=searchbox]'));
  });

  it('returns focus to the trigger on Tab from the search box and marks it touched', async () => {
    const { fixture, combobox, host } = await setup(true);
    combobox.click();
    await fixture.whenStable();
    const search = document.querySelector<HTMLInputElement>('[role=searchbox]')!;
    search.value = 'company 3';
    search.dispatchEvent(new Event('input'));
    search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    await fixture.whenStable();
    expect(combobox.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(combobox);
    expect(host.company.touched).toBe(true);
    expect(host.searches.at(-1)).toBe('');
  });

  it('marks the field touched when closed by an outside click', async () => {
    const { fixture, combobox, host } = await setup(false);
    combobox.click();
    await fixture.whenStable();
    document.getElementById('outside')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();
    expect(combobox.getAttribute('aria-expanded')).toBe('false');
    expect(host.company.touched).toBe(true);
  });

  it('scrolls the active option into view', async () => {
    const spy = vi.spyOn(Element.prototype, 'scrollIntoView');
    const { fixture, combobox } = await setup(false);
    combobox.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await fixture.whenStable();
    combobox.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await fixture.whenStable();
    const active = combobox.getAttribute('aria-activedescendant')!;
    expect(spy.mock.contexts.some((el) => (el as Element).id === active)).toBe(true);
    spy.mockRestore();
  });

  it('shows a bound value that is missing from the options instead of the placeholder', async () => {
    const { fixture, combobox, host } = await setup(false);
    host.company.setValue('9999');
    await fixture.whenStable();
    expect(combobox.textContent).toContain('9999');
  });
});

@Component({
  imports: [IamDialog],
  template: `<iam-dialog heading="Edit request"
    ><button type="button" class="first">First</button></iam-dialog
  >`,
})
class EditDialog {}

@Component({ template: `` })
class DialogHost {
  readonly dialogs = TestBed.inject(IamDialogService);
  readonly confirmations = TestBed.inject(IamConfirmService);
  readonly anchor = viewChild<never>('none');
}

describe('dialogs (review fixes)', () => {
  afterEach(clearOverlays);

  async function setup() {
    const fixture = TestBed.createComponent(DialogHost);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    return { fixture, host: fixture.componentInstance };
  }

  it('describes a confirmation by its message', async () => {
    const { fixture, host } = await setup();
    void host.confirmations.confirm({
      heading: 'Revoke?',
      message: 'This cannot be undone.',
      destructive: true,
    });
    await fixture.whenStable();
    const dialog = document.querySelector('[role=alertdialog]')!;
    const message = dialog.querySelector('.iam-confirm__message')!;
    expect(dialog.getAttribute('aria-describedby')).toBe(message.id);
  });

  it('focuses the heading first, not the close button', async () => {
    const { fixture, host } = await setup();
    host.dialogs.open(EditDialog);
    await fixture.whenStable();
    await new Promise((r) => setTimeout(r));
    expect(document.activeElement?.tagName).toBe('H2');
  });

  it('stays open on Escape when disableClose is set', async () => {
    const { fixture, host } = await setup();
    const ref = host.dialogs.open(EditDialog, { disableClose: true });
    await fixture.whenStable();
    document
      .querySelector('[role=dialog]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    await fixture.whenStable();
    expect(document.querySelector('[role=dialog]')).not.toBeNull();
    ref.close();
  });
});
