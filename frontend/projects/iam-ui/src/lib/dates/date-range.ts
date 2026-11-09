import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  effect,
  inject,
  input,
  untracked,
  viewChildren,
} from '@angular/core';
import { AbstractControl, FormsModule, ValidationErrors } from '@angular/forms';
import { IAM_FIELD_CONTEXT, iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';
import { IsoDate, formatIsoDate, isIsoDate } from './date-only';
import { IamDatePicker } from './date-picker';

/** A date range; both ends inclusive, either may be null until chosen. */
export interface IamDateRangeValue {
  readonly start: IsoDate | null;
  readonly end: IsoDate | null;
}

const EMPTY: IamDateRangeValue = { start: null, end: null };

/**
 * Start and end dates (S1-10). Both are inclusive date-only values (`yyyy-MM-dd`); the end may not be before the start,
 * and each picker's calendar is limited accordingly. Carries its own legend and errors like a radio group. With
 * `required`, both dates must be present.
 */
@Component({
  selector: 'iam-date-range',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IamDatePicker],
  // The inner pickers belong to this range, not to any enclosing form field.
  providers: [{ provide: IAM_FIELD_CONTEXT, useValue: null }],
  template: `
    <fieldset
      class="iam-range"
      role="group"
      tabindex="-1"
      [id]="inputId()"
      [attr.aria-describedby]="groupDescribedBy()"
      [attr.aria-invalid]="errors().length > 0 ? 'true' : null"
      [disabled]="isDisabled()"
    >
      <legend class="iam-range__legend">
        {{ legend() }}
        @if (required()) {
          <span aria-hidden="true" class="iam-range__required">*</span>
          <span class="iam-visually-hidden">(required)</span>
        }
      </legend>
      @if (hint()) {
        <p class="iam-range__hint" [id]="hintId">{{ hint() }}</p>
      }
      <div class="iam-range__fields">
        <div class="iam-range__field">
          <label class="iam-range__label" [for]="startId">{{ startLabel() }}</label>
          <iam-date-picker
            [controlId]="startId"
            [ngModel]="value().start"
            (ngModelChange)="update({ start: $event, end: value().end })"
            (focusout)="markTouched()"
            [min]="min()"
            [max]="value().end ?? max()"
            [disabled]="isDisabled()"
            [describedByExtra]="errorsId()"
            openLabel="Choose start date"
          />
        </div>
        <div class="iam-range__field">
          <label class="iam-range__label" [for]="endId">{{ endLabel() }}</label>
          <iam-date-picker
            [controlId]="endId"
            [ngModel]="value().end"
            (ngModelChange)="update({ start: value().start, end: $event })"
            (focusout)="markTouched()"
            [min]="value().start ?? min()"
            [max]="max()"
            [disabled]="isDisabled()"
            [describedByExtra]="errorsId()"
            openLabel="Choose end date"
          />
        </div>
      </div>
      @if (errors().length > 0) {
        <ul class="iam-range__errors" [id]="errorId">
          @for (message of errors(); track $index) {
            <li>{{ message }}</li>
          }
        </ul>
      }
    </fieldset>
  `,
  styles: `
    :host {
      display: block;
      font-family: var(--iam-font-family);
      color: var(--iam-color-text);
    }
    .iam-range {
      margin: 0;
      padding: 0;
      border: 0;
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-2);
    }
    .iam-range__legend {
      padding: 0;
      margin-bottom: var(--iam-space-1);
      font-size: var(--iam-font-size-sm);
      font-weight: var(--iam-font-weight-semibold);
    }
    .iam-range__required {
      color: var(--iam-color-danger);
      margin-inline-start: var(--iam-space-1);
    }
    .iam-range__hint {
      margin: 0;
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-text-muted);
    }
    .iam-range__fields {
      display: flex;
      flex-wrap: wrap;
      gap: var(--iam-space-4);
    }
    .iam-range__field {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-1);
      flex: 1 1 12rem;
    }
    .iam-range__label {
      font-size: var(--iam-font-size-sm);
    }
    .iam-range__errors {
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-danger);
    }
  `,
})
export class IamDateRange extends IamValueControl<IamDateRangeValue> {
  readonly legend = input.required<string>();
  readonly startLabel = input<string>('Start date');
  readonly endLabel = input<string>('End date');
  readonly hint = input<string>('');
  readonly required = input<boolean>(false);
  readonly min = input<IsoDate | null>(null);
  readonly max = input<IsoDate | null>(null);
  /** Server-side messages (ProblemDetails) for this range. Plain text. */
  readonly serverErrors = input<readonly string[] | null | undefined>([]);

  protected override readonly validates = true;
  protected readonly startId = iamId('iam-range-start');
  protected readonly endId = iamId('iam-range-end');
  protected readonly hintId = iamId('iam-hint');
  protected readonly errorId = iamId('iam-error');
  private readonly pickers = viewChildren(IamDatePicker);
  private readonly locale = inject(LOCALE_ID);

  /** Validity depends on these inputs: re-run it when they change. */
  private readonly rulesWatch = effect(() => {
    this.min();
    this.max();
    this.required();
    untracked(() => this.revalidate());
  });

  protected readonly errors = computed<readonly string[]>(() => {
    this.revision();
    const messages: string[] = [];
    const control = this.control;
    if (this.touched() && control?.errors) {
      for (const detail of Object.values(control.errors)) {
        if (typeof detail === 'string') {
          messages.push(detail);
        }
      }
    }

    messages.push(...(this.serverErrors() ?? []));
    return messages;
  });

  protected readonly errorsId = computed(() => (this.errors().length > 0 ? this.errorId : null));
  protected readonly groupDescribedBy = computed(
    () => [this.hint() ? this.hintId : null, this.errorsId()].filter(Boolean).join(' ') || null,
  );

  protected update(next: IamDateRangeValue): void {
    this.commit({ start: next.start ?? null, end: next.end ?? null });
    this.revalidate();
  }

  protected override markTouched(): void {
    super.markTouched();
    this.revalidate();
  }

  protected override validate(control: AbstractControl): ValidationErrors | null {
    void control;
    if (this.pickers().some((p) => p.hasParseError())) {
      return { iamDate: 'Enter real dates in the format shown.' };
    }

    const { start, end } = this.value();
    if (this.required() && (!start || !end)) {
      return { iamDateRangeRequired: `${this.legend()} needs both a start and an end date.` };
    }

    const min = this.min();
    const max = this.max();
    if ((min && start && start < min) || (min && end && end < min)) {
      return { iamDateMin: `Dates must be on or after ${formatIsoDate(min, this.locale)}.` };
    }

    if ((max && start && start > max) || (max && end && end > max)) {
      return { iamDateMax: `Dates must be on or before ${formatIsoDate(max, this.locale)}.` };
    }

    if (start && end && end < start) {
      return { iamDateRange: 'The end date must be on or after the start date.' };
    }

    return null;
  }

  /**
   * A value written by the form (patch or reset) replaces whatever the user typed, including text that was not a
   * date: the inner pickers are written directly because ngModel skips a write when the value (null) is unchanged.
   */
  override writeValue(value: IamDateRangeValue | null | undefined): void {
    super.writeValue(value);
    const { start, end } = this.value();
    const [startPicker, endPicker] = this.pickers();
    startPicker?.writeValue(start);
    endPicker?.writeValue(end);
  }

  protected emptyValue(): IamDateRangeValue {
    return EMPTY;
  }

  protected normalize(value: IamDateRangeValue | null | undefined): IamDateRangeValue {
    return {
      start: isIsoDate(value?.start) ? (value!.start as IsoDate) : null,
      end: isIsoDate(value?.end) ? (value!.end as IsoDate) : null,
    };
  }
}
