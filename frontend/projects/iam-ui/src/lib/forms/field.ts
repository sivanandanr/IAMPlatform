import { InjectionToken, Signal, computed, signal } from '@angular/core';
import { AbstractControl, ValidationErrors, Validators } from '@angular/forms';

let nextId = 0;

/** Unique, stable DOM id for one rendering (never derived from user data). */
export function iamId(prefix: string): string {
  nextId += 1;
  return `${prefix}-${nextId}`;
}

/** Ids a caller may set (letters, digits, '-' and '_', starting with a letter); anything else is ignored. */
export const IAM_SAFE_ID = /^[A-Za-z][A-Za-z0-9_-]{0,99}$/;

/** The caller's id if it is safe, otherwise null. */
export function safeId(id: string | null | undefined): string | null {
  return id && IAM_SAFE_ID.test(id) ? id : null;
}

/** A control registered with its form field, so the field can show its state. */
export interface IamFieldControl {
  /** The bound form control, if any (reactive forms or ngModel). */
  readonly control: AbstractControl | null;
  /** True once the user has left the control, or the form was submitted. */
  readonly touched: Signal<boolean>;
}

/**
 * Wiring between `iam-form-field` and the one control inside it: shared ids for label, hint and errors, the label
 * text (used in messages) and the error list the control must reference with aria-describedby / aria-invalid.
 */
export class IamFieldContext {
  private readonly generatedInputId = iamId('iam-input');
  /** Optional caller-chosen id (validated), so a validation summary can link to the control. */
  readonly requestedInputId = signal<string | null>(null);
  readonly inputId = computed(() => safeId(this.requestedInputId()) ?? this.generatedInputId);
  readonly labelId = iamId('iam-label');
  readonly hintId = iamId('iam-hint');
  readonly errorId = iamId('iam-error');

  readonly label = signal('');
  readonly hasHint = signal(false);
  /** Explicit `required` from the field; when unset, read from the control's validators. */
  readonly requiredOverride = signal<boolean | undefined>(undefined);
  readonly serverErrors = signal<readonly string[]>([]);
  readonly registered = signal<IamFieldControl | null>(null);

  /** Bumped by the control on every value or status change, so errors are re-read. */
  readonly revision = signal(0);

  /** Required marker and aria-required: explicit input, else the Validators.required on the bound control. */
  readonly required = computed(() => {
    this.revision();
    const explicit = this.requiredOverride();
    if (explicit !== undefined) {
      return explicit;
    }

    const control = this.registered()?.control;
    return (
      !!control &&
      (control.hasValidator(Validators.required) || control.hasValidator(Validators.requiredTrue))
    );
  });

  /** Messages to show: client validation once touched, plus server errors (always). */
  readonly errors = computed<readonly string[]>(() => {
    this.revision();
    const field = this.registered();
    const messages: string[] = [];
    if (field?.control && field.touched() && field.control.errors) {
      messages.push(...validationMessages(this.label(), field.control.errors));
    }

    messages.push(...this.serverErrors());
    return messages;
  });

  readonly invalid = computed(() => this.errors().length > 0);

  /** Space-separated ids for aria-describedby: hint first, then errors. */
  readonly describedBy = computed(
    () =>
      [this.hasHint() ? this.hintId : null, this.invalid() ? this.errorId : null]
        .filter((id) => id !== null)
        .join(' ') || null,
  );
}

export const IAM_FIELD_CONTEXT = new InjectionToken<IamFieldContext>('IAM_FIELD_CONTEXT');

/**
 * Human-readable messages for Angular's built-in validators. Values are rendered as text by the templates; nothing
 * here is treated as HTML.
 */
export function validationMessages(label: string, errors: ValidationErrors): string[] {
  const name = label || 'This field';
  const messages: string[] = [];
  for (const [key, detail] of Object.entries(errors)) {
    switch (key) {
      case 'required':
        messages.push(`${name} is required.`);
        break;
      case 'maxlength':
        messages.push(
          `${name} must be at most ${(detail as { requiredLength: number }).requiredLength} characters.`,
        );
        break;
      case 'minlength':
        messages.push(
          `${name} must be at least ${(detail as { requiredLength: number }).requiredLength} characters.`,
        );
        break;
      case 'email':
        messages.push(`${name} must be a valid email address.`);
        break;
      case 'pattern':
        messages.push(`${name} has an invalid format.`);
        break;
      case 'min':
        messages.push(`${name} must be at least ${(detail as { min: number }).min}.`);
        break;
      case 'max':
        messages.push(`${name} must be at most ${(detail as { max: number }).max}.`);
        break;
      default:
        messages.push(typeof detail === 'string' ? detail : `${name} is invalid.`);
    }
  }

  return messages;
}
