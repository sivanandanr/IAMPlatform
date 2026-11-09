import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Validators } from '@angular/forms';
import { iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';

const CHOICE_STYLES = `
  :host { display: block; font-family: var(--iam-font-family); color: var(--iam-color-text); }
  .iam-choice { display: inline-flex; align-items: center; gap: var(--iam-space-2); min-height: var(--iam-control-height); cursor: pointer; font-size: var(--iam-font-size-md); }
  .iam-choice input { width: 1.125rem; height: 1.125rem; margin: 0; accent-color: var(--iam-color-primary); cursor: inherit; }
  .iam-choice input:focus-visible { outline: var(--iam-focus-ring-width) solid var(--iam-color-focus); outline-offset: var(--iam-focus-ring-offset); }
  .iam-choice--disabled { color: var(--iam-color-disabled-text); cursor: not-allowed; }
  .iam-choice__hint { margin: 0 0 0 calc(1.125rem + var(--iam-space-2)); font-size: var(--iam-font-size-sm); color: var(--iam-color-text-muted); }
  .iam-choice__error { margin: 0; font-size: var(--iam-font-size-sm); color: var(--iam-color-danger); }
  .iam-choice__errors { padding: 0; list-style: none; }
`;

/**
 * Native checkbox with its own visible label (a checkbox carries its label; `iam-form-field` is not needed).
 * The value is a boolean.
 */
@Component({
  selector: 'iam-checkbox',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="iam-choice" [class.iam-choice--disabled]="isDisabled()">
      <input
        type="checkbox"
        [id]="inputId()"
        [checked]="value()"
        [disabled]="isDisabled()"
        [attr.aria-invalid]="field ? ariaInvalid() : ownInvalid()"
        [attr.aria-describedby]="describedBy()"
        (change)="onCheckedChange($event)"
        (blur)="markTouched()"
      />
      <span>{{ label() }}</span>
    </label>
    @if (hint()) {
      <p class="iam-choice__hint" [id]="hintId">{{ hint() }}</p>
    }
    @if (!field && ownErrors().length > 0) {
      <ul class="iam-choice__error iam-choice__errors" [id]="errorId">
        @for (message of ownErrors(); track $index) {
          <li>{{ message }}</li>
        }
      </ul>
    }
  `,
  styles: CHOICE_STYLES,
})
export class IamCheckbox extends IamValueControl<boolean> {
  readonly label = input.required<string>();
  readonly hint = input<string>('');
  /** Server-side messages when the checkbox is not inside an iam-form-field. Plain text. */
  readonly serverErrors = input<readonly string[] | null | undefined>([]);

  protected readonly hintId = iamId('iam-hint');
  protected readonly errorId = iamId('iam-error');

  /**
   * Errors the checkbox shows itself when not inside an iam-form-field: "must be checked" for Validators.requiredTrue
   * or required, once touched, plus server messages.
   */
  protected readonly ownErrors = computed<readonly string[]>(() => {
    this.revision();
    const messages: string[] = [];
    if (this.touched() && this.control?.errors?.['required']) {
      messages.push(`${this.label()} must be checked.`);
    }

    messages.push(...(this.serverErrors() ?? []));
    return messages;
  });

  protected readonly ownInvalid = computed(() => (this.ownErrors().length > 0 ? 'true' : null));

  /** Own hint, own errors, plus any field hint/error ids. */
  protected readonly describedBy = computed(
    () =>
      [
        this.hint() ? this.hintId : null,
        !this.field && this.ownErrors().length > 0 ? this.errorId : null,
        this.ariaDescribedBy(),
      ]
        .filter(Boolean)
        .join(' ') || null,
  );

  protected onCheckedChange(event: Event): void {
    this.commit((event.target as HTMLInputElement).checked);
  }

  protected emptyValue(): boolean {
    return false;
  }

  protected normalize(value: boolean | null | undefined): boolean {
    return value === true;
  }
}

/**
 * On/off switch for settings that take effect immediately. A `<button role="switch">` with aria-checked; Space and
 * Enter toggle it (native button behaviour).
 */
@Component({
  selector: 'iam-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      role="switch"
      class="iam-toggle"
      [id]="inputId()"
      [attr.aria-checked]="value() ? 'true' : 'false'"
      [attr.aria-describedby]="ariaDescribedBy()"
      [disabled]="isDisabled()"
      (click)="toggle()"
      (blur)="markTouched()"
    >
      <span class="iam-toggle__track" aria-hidden="true"
        ><span class="iam-toggle__thumb"></span
      ></span>
      <span class="iam-toggle__label">{{ label() }}</span>
      <span class="iam-toggle__state" aria-hidden="true">{{ value() ? onText() : offText() }}</span>
    </button>
  `,
  styles: `
    :host {
      display: block;
      font-family: var(--iam-font-family);
    }
    .iam-toggle {
      display: inline-flex;
      align-items: center;
      gap: var(--iam-space-2);
      min-height: var(--iam-control-height);
      padding: 0;
      border: 0;
      background: none;
      color: var(--iam-color-text);
      font: inherit;
      font-size: var(--iam-font-size-md);
      cursor: pointer;
    }
    .iam-toggle:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
      border-radius: var(--iam-radius-sm);
    }
    .iam-toggle:disabled {
      color: var(--iam-color-disabled-text);
      cursor: not-allowed;
    }
    .iam-toggle__track {
      position: relative;
      width: 2.25rem;
      height: 1.25rem;
      border-radius: 999px;
      background: var(--iam-color-surface);
      border: var(--iam-border-width) solid var(--iam-color-border);
      transition: background-color var(--iam-motion-duration) var(--iam-motion-easing);
    }
    .iam-toggle__thumb {
      position: absolute;
      top: 0.125rem;
      left: 0.125rem;
      width: 0.875rem;
      height: 0.875rem;
      border-radius: 50%;
      background: var(--iam-color-border);
      transition: transform var(--iam-motion-duration) var(--iam-motion-easing);
    }
    .iam-toggle[aria-checked='true'] .iam-toggle__track {
      background: var(--iam-color-primary);
      border-color: var(--iam-color-primary);
    }
    .iam-toggle[aria-checked='true'] .iam-toggle__thumb {
      transform: translateX(1rem);
      background: var(--iam-color-on-primary);
    }
    .iam-toggle__state {
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-text-muted);
    }
  `,
})
export class IamToggle extends IamValueControl<boolean> {
  readonly label = input.required<string>();
  readonly onText = input<string>('On');
  readonly offText = input<string>('Off');

  protected toggle(): void {
    this.commit(!this.value());
  }

  protected emptyValue(): boolean {
    return false;
  }

  protected normalize(value: boolean | null | undefined): boolean {
    return value === true;
  }
}

/** One option of a radio group or dropdown. `value` is compared with ===; `label` is shown as text. */
export interface IamOption<T> {
  readonly value: T;
  readonly label: string;
  readonly disabled?: boolean;
}

/**
 * A set of native radio buttons in a fieldset with a legend. Arrow keys move between options (native behaviour).
 */
@Component({
  selector: 'iam-radio-group',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset
      tabindex="-1"
      [id]="inputId()"
      class="iam-radio-group"
      role="radiogroup"
      [attr.aria-required]="isRequired() ? 'true' : null"
      [attr.aria-describedby]="describedBy()"
      [attr.aria-invalid]="groupInvalid()"
      [disabled]="isDisabled()"
    >
      <legend class="iam-radio-group__legend">
        {{ legend() }}
        @if (isRequired()) {
          <span aria-hidden="true" class="iam-radio-group__required">*</span>
          <span class="iam-visually-hidden">(required)</span>
        }
      </legend>
      @if (hint()) {
        <p class="iam-choice__hint iam-radio-group__hint" [id]="hintId">{{ hint() }}</p>
      }
      @for (option of options(); track $index) {
        <label class="iam-choice" [class.iam-choice--disabled]="isDisabled() || option.disabled">
          <input
            type="radio"
            [name]="groupName"
            [checked]="option.value === value()"
            [disabled]="option.disabled ?? false"
            (change)="select(option.value)"
            (blur)="markTouched()"
          />
          <span>{{ option.label }}</span>
        </label>
      }
      @if (errors().length > 0) {
        <ul class="iam-choice__error iam-radio-group__errors" [id]="errorId">
          @for (message of errors(); track $index) {
            <li>{{ message }}</li>
          }
        </ul>
      }
    </fieldset>
  `,
  styles: `
    ${CHOICE_STYLES}
    .iam-radio-group {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-1);
      margin: 0;
      padding: 0;
      border: 0;
    }
    .iam-radio-group__legend {
      padding: 0;
      margin-bottom: var(--iam-space-1);
      font-size: var(--iam-font-size-sm);
      font-weight: var(--iam-font-weight-semibold);
    }
    .iam-radio-group__required {
      color: var(--iam-color-danger);
      margin-inline-start: var(--iam-space-1);
    }
    .iam-radio-group__hint {
      margin-left: 0;
    }
    .iam-radio-group__errors {
      list-style: none;
      padding: 0;
    }
  `,
})
export class IamRadioGroup<T = string> extends IamValueControl<T | null> {
  readonly legend = input.required<string>();
  readonly options = input.required<readonly IamOption<T>[]>();
  readonly hint = input<string>('');
  /** Overrides the required marker; by default it follows Validators.required on the bound control. */
  readonly required = input<boolean | undefined>(undefined);
  /** Server-side messages (ProblemDetails) for this group. Plain text. */
  readonly serverErrors = input<readonly string[] | null | undefined>([]);

  protected readonly groupName = iamId('iam-radio');
  protected readonly hintId = iamId('iam-hint');
  protected readonly errorId = iamId('iam-error');

  protected readonly isRequired = computed(() => {
    this.revision();
    const explicit = this.required();
    return explicit ?? !!this.control?.hasValidator(Validators.required);
  });

  /** The group carries its own legend and errors (a fieldset cannot sit inside a label). */
  protected readonly errors = computed<readonly string[]>(() => {
    this.revision();
    const messages: string[] = [];
    const control = this.control;
    if (control && this.touched() && control.errors?.['required']) {
      messages.push(`${this.legend()} is required.`);
    }

    messages.push(...(this.serverErrors() ?? []));
    return messages;
  });

  protected readonly describedBy = computed(
    () =>
      [this.hint() ? this.hintId : null, this.errors().length > 0 ? this.errorId : null]
        .filter(Boolean)
        .join(' ') || null,
  );

  protected readonly groupInvalid = computed(() => (this.errors().length > 0 ? 'true' : null));

  protected select(value: T): void {
    this.commit(value);
  }

  protected emptyValue(): T | null {
    return null;
  }

  protected normalize(value: T | null | undefined): T | null {
    return value ?? null;
  }
}
