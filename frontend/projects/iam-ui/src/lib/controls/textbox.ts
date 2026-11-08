import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { IamValueControl } from '../forms/value-control';

export type IamTextboxType = 'text' | 'email' | 'search' | 'tel' | 'url';

/** How surrounding white space is removed when the user leaves the field. Never strips leading zeros or inner text. */
export type IamTrimPolicy = 'none' | 'both';

export const FIELD_INPUT_STYLES = `
  :host { display: block; }
  .iam-input {
    box-sizing: border-box; width: 100%; min-height: var(--iam-control-height);
    padding: var(--iam-space-2) var(--iam-control-padding-x);
    border: var(--iam-border-width) solid var(--iam-color-border); border-radius: var(--iam-radius-md);
    background: var(--iam-color-surface-raised); color: var(--iam-color-text);
    font: inherit; font-family: var(--iam-font-family); font-size: var(--iam-font-size-md); line-height: var(--iam-line-height);
  }
  .iam-input:focus-visible { outline: var(--iam-focus-ring-width) solid var(--iam-color-focus); outline-offset: var(--iam-focus-ring-offset); }
  .iam-input[aria-invalid='true'] { border-color: var(--iam-color-danger); border-width: 2px; }
  /* Only real inputs: a div (the dropdown trigger) always matches :read-only. */
  .iam-input:is(input, textarea):is(:disabled, :read-only) { background: var(--iam-color-disabled-bg); color: var(--iam-color-disabled-text); }
  .iam-input-affix { display: flex; align-items: stretch; }
  .iam-input-affix__text { display: inline-flex; align-items: center; padding: 0 var(--iam-space-2); color: var(--iam-color-text-muted); background: var(--iam-color-surface); border: var(--iam-border-width) solid var(--iam-color-border); }
  .iam-input-affix__text--prefix { border-inline-end: 0; border-radius: var(--iam-radius-md) 0 0 var(--iam-radius-md); }
  .iam-input-affix__text--suffix { border-inline-start: 0; border-radius: 0 var(--iam-radius-md) var(--iam-radius-md) 0; }
`;

/**
 * Single-line text input. Use inside `iam-form-field`, which supplies the label, hint and errors.
 * `maxLength` is required: every text input is bounded (the server validates again).
 */
@Component({
  selector: 'iam-textbox',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="iam-input-affix">
      @if (prefix()) {
        <span class="iam-input-affix__text iam-input-affix__text--prefix" aria-hidden="true">{{
          prefix()
        }}</span>
      }
      <input
        class="iam-input"
        [id]="inputId()"
        [type]="type()"
        [value]="value()"
        [attr.maxlength]="maxLength()"
        [attr.inputmode]="inputMode()"
        [attr.autocomplete]="autocomplete()"
        [attr.placeholder]="placeholder() || null"
        [attr.aria-invalid]="ariaInvalid()"
        [attr.aria-describedby]="ariaDescribedBy()"
        [attr.aria-required]="ariaRequired()"
        [attr.aria-label]="field ? null : ariaLabel()"
        [disabled]="isDisabled()"
        [readOnly]="readonly()"
        (input)="onInput($event)"
        (blur)="onBlur()"
      />
      @if (suffix()) {
        <span class="iam-input-affix__text iam-input-affix__text--suffix" aria-hidden="true">{{
          suffix()
        }}</span>
      }
    </div>
  `,
  styles: FIELD_INPUT_STYLES,
})
export class IamTextbox extends IamValueControl<string> {
  readonly type = input<IamTextboxType>('text');
  readonly maxLength = input.required<number>();
  readonly trim = input<IamTrimPolicy>('both');
  readonly inputMode = input<string | null>(null);
  readonly autocomplete = input<string>('off');
  readonly placeholder = input<string>('');
  readonly prefix = input<string>('');
  readonly suffix = input<string>('');
  readonly readonly = input<boolean>(false);
  readonly disabled = input<boolean>(false);
  /** Accessible name when used without `iam-form-field` (for example in a toolbar search box). */
  readonly ariaLabel = input<string | null>(null);

  protected readonly isDisabled = computed(() => this.disabled() || this.disabledByForm());

  protected onInput(event: Event): void {
    this.commit((event.target as HTMLInputElement).value);
  }

  protected onBlur(): void {
    if (this.trim() === 'both') {
      const trimmed = this.value().trim();
      if (trimmed !== this.value()) {
        this.commit(trimmed);
      }
    }

    this.markTouched();
  }

  protected emptyValue(): string {
    return '';
  }

  protected normalize(value: string | null | undefined): string {
    return value ?? '';
  }
}
