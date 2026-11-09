import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';
import { FIELD_INPUT_STYLES, IamTrimPolicy } from './textbox';

/**
 * Multi-line text input with a visible character counter. `maxLength` is required. The height grows with the text
 * between `minRows` and `maxRows` (CSS field-sizing where supported, otherwise the min height with scrolling).
 */
@Component({
  selector: 'iam-textarea',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <textarea
      class="iam-input iam-textarea"
      [id]="inputId()"
      [value]="value()"
      [attr.maxlength]="maxLength()"
      [attr.rows]="minRows()"
      [style.min-height.lh]="minRows()"
      [style.max-height.lh]="maxRows()"
      [attr.aria-invalid]="ariaInvalid()"
      [attr.aria-describedby]="describedBy()"
      [attr.aria-required]="ariaRequired()"
      [attr.aria-label]="field ? null : ariaLabel()"
      [disabled]="isDisabled()"
      [readOnly]="readonly()"
      (input)="onInput($event)"
      (blur)="onBlur()"
    ></textarea>
    <p
      class="iam-textarea__counter"
      [id]="counterId"
      [class.iam-textarea__counter--near]="nearLimit()"
    >
      {{ value().length }} / {{ maxLength() }}
      <span class="iam-visually-hidden">characters</span>
    </p>
  `,
  styles: `
    ${FIELD_INPUT_STYLES}
    .iam-textarea {
      resize: vertical;
      field-sizing: content;
      overflow-y: auto;
    }
    .iam-textarea__counter {
      margin: var(--iam-space-1) 0 0;
      text-align: end;
      font-size: var(--iam-font-size-xs);
      color: var(--iam-color-text-muted);
    }
    .iam-textarea__counter--near {
      color: var(--iam-color-warning);
      font-weight: var(--iam-font-weight-semibold);
    }
  `,
})
export class IamTextarea extends IamValueControl<string> {
  readonly maxLength = input.required<number>();
  readonly minRows = input<number>(3);
  readonly maxRows = input<number>(10);
  readonly trim = input<IamTrimPolicy>('both');
  readonly readonly = input<boolean>(false);
  readonly ariaLabel = input<string | null>(null);

  protected readonly counterId = iamId('iam-counter');
  protected readonly nearLimit = computed(() => this.value().length >= this.maxLength() * 0.9);
  protected readonly describedBy = computed(() =>
    [this.ariaDescribedBy(), this.counterId].filter(Boolean).join(' '),
  );

  protected onInput(event: Event): void {
    this.commit((event.target as HTMLTextAreaElement).value);
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
