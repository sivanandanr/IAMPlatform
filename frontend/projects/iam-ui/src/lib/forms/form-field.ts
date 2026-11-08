import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { IAM_FIELD_CONTEXT, IamFieldContext } from './field';

/**
 * Label, hint and errors around exactly one IAM control. Provides the ids the control uses for aria-labelledby,
 * aria-describedby and aria-invalid. Server-side validation messages (ProblemDetails) are passed in `serverErrors`
 * and rendered as plain text.
 *
 * ```html
 * <iam-form-field label="Display name" hint="As shown to approvers" [required]="true" [serverErrors]="errors.name">
 *   <iam-textbox formControlName="name" [maxLength]="200" />
 * </iam-form-field>
 * ```
 */
@Component({
  selector: 'iam-form-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: IAM_FIELD_CONTEXT, useFactory: () => new IamFieldContext() }],
  host: { class: 'iam-form-field' },
  template: `
    <label class="iam-form-field__label" [id]="field.labelId" [attr.for]="field.inputId()">
      {{ label() }}
      @if (field.required()) {
        <span class="iam-form-field__required" aria-hidden="true">*</span>
        <span class="iam-visually-hidden">(required)</span>
      }
    </label>
    @if (hint()) {
      <p class="iam-form-field__hint" [id]="field.hintId">{{ hint() }}</p>
    }
    <ng-content />
    @if (field.invalid()) {
      <ul class="iam-form-field__errors" [id]="field.errorId">
        @for (message of field.errors(); track $index) {
          <li>{{ message }}</li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-1);
      font-family: var(--iam-font-family);
    }
    .iam-form-field__label {
      font-size: var(--iam-font-size-sm);
      font-weight: var(--iam-font-weight-semibold);
      color: var(--iam-color-text);
    }
    .iam-form-field__required {
      color: var(--iam-color-danger);
      margin-inline-start: var(--iam-space-1);
    }
    .iam-form-field__hint {
      margin: 0;
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-text-muted);
    }
    .iam-form-field__errors {
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-danger);
    }
  `,
})
export class IamFormField {
  readonly label = input.required<string>();
  readonly hint = input<string>('');
  /** Overrides the required marker; by default it follows Validators.required on the bound control. */
  readonly required = input<boolean | undefined>(undefined);
  /** Optional id for the control (letters, digits, '-', '_'), so a validation summary can link to it. */
  readonly controlId = input<string | null>(null);
  /** Server-side messages for this field, e.g. from ProblemDetails `errors[field]`. Plain text only. */
  readonly serverErrors = input<readonly string[] | null | undefined>([]);

  protected readonly field = inject(IAM_FIELD_CONTEXT);

  constructor() {
    effect(() => {
      this.field.label.set(this.label());
      this.field.hasHint.set(this.hint().length > 0);
      this.field.requiredOverride.set(this.required());
      this.field.requestedInputId.set(this.controlId());
      this.field.serverErrors.set(this.serverErrors() ?? []);
    });
  }
}
