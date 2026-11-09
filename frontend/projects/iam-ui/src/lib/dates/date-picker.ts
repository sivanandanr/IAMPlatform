import { CdkTrapFocus } from '@angular/cdk/a11y';
import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  LOCALE_ID,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { AbstractControl, ValidationErrors } from '@angular/forms';
import { IamIconButton } from '../controls/button';
import { FIELD_INPUT_STYLES } from '../controls/textbox';
import { iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';
import {
  IsoDate,
  addDays,
  addMonths,
  clampDate,
  firstDayOfWeek,
  formatIsoDate,
  isIsoDate,
  monthGrid,
  monthLabel,
  parseUserDate,
  patternHint,
  todayIso,
  toParts,
  weekdayNames,
} from './date-only';

const CALENDAR_MIN: IsoDate = '1000-01-01';
const CALENDAR_MAX: IsoDate = '9999-12-31';

const POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 4 },
  { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -4 },
];

/**
 * Date-only input (S1-10): the value is an ISO `yyyy-MM-dd` string or null, never a Date, so it cannot shift with time
 * zones. The user types in their locale's numeric format (or ISO) or picks from a calendar dialog (WAI-ARIA date picker
 * dialog: arrows move by day/week, Home/End to week start/end, PageUp/PageDown by month, Shift+PageUp/PageDown by year,
 * Enter/Space selects, Escape closes; focus is trapped in the dialog and returned on close). `min`/`max` are inclusive.
 * Unparsable text and out-of-range dates are reported as validation errors on the bound form control.
 */
@Component({
  selector: 'iam-date-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin, CdkTrapFocus, IamIconButton],
  template: `
    <div class="iam-date" cdkOverlayOrigin #origin="cdkOverlayOrigin">
      <input
        #textInput
        class="iam-input iam-date__input"
        type="text"
        inputmode="numeric"
        autocomplete="off"
        [id]="inputId()"
        [value]="text()"
        [attr.placeholder]="pattern()"
        [attr.aria-describedby]="describedBy()"
        [attr.aria-invalid]="ariaInvalid()"
        [attr.aria-required]="ariaRequired()"
        [attr.aria-label]="field ? null : ariaLabel()"
        [disabled]="isDisabled()"
        [readOnly]="readonly()"
        (input)="onText($event)"
        (blur)="onBlur()"
        (keydown)="onInputKeydown($event)"
      />
      <button
        #toggleButton
        iamIconButton
        class="iam-date__toggle"
        [label]="openLabel()"
        aria-haspopup="dialog"
        [attr.aria-expanded]="open() ? 'true' : 'false'"
        [disabled]="isDisabled() || readonly()"
        (click)="toggle()"
      >
        <svg
          aria-hidden="true"
          width="16"
          height="16"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          stroke-width="1.5"
        >
          <rect x="1.5" y="2.5" width="13" height="12" rx="1.5" />
          <path d="M1.5 6.5h13M5 1v3M11 1v3" />
        </svg>
      </button>
    </div>
    <p class="iam-date__format" [id]="formatId">Format: {{ pattern() }}</p>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      (overlayOutsideClick)="close(false)"
      (detach)="close(false)"
    >
      <div
        class="iam-calendar"
        role="dialog"
        aria-modal="true"
        [attr.aria-label]="dialogLabel()"
        cdkTrapFocus
        (keydown)="onCalendarKeydown($event)"
      >
        <div class="iam-calendar__header">
          <button
            iamIconButton
            label="Previous month"
            (click)="moveFocus(addMonths(focusDate(), -1))"
          >
            <span aria-hidden="true">&#8249;</span>
          </button>
          <h2 class="iam-calendar__title" [id]="captionId" aria-live="polite">{{ caption() }}</h2>
          <button iamIconButton label="Next month" (click)="moveFocus(addMonths(focusDate(), 1))">
            <span aria-hidden="true">&#8250;</span>
          </button>
        </div>
        <table class="iam-calendar__grid" role="grid" [attr.aria-labelledby]="captionId">
          <thead>
            <tr>
              @for (day of weekdays(); track $index) {
                <th scope="col" [attr.abbr]="day.long">{{ day.short }}</th>
              }
            </tr>
          </thead>
          <tbody>
            @for (week of weeks(); track $index) {
              <tr>
                @for (day of week; track day) {
                  <td
                    class="iam-calendar__day"
                    [id]="dayId(day)"
                    [class.iam-calendar__day--outside]="!inMonth(day)"
                    [class.iam-calendar__day--today]="day === today()"
                    [attr.tabindex]="day === focusDate() ? 0 : -1"
                    [attr.aria-selected]="day === value() ? 'true' : 'false'"
                    [attr.aria-current]="day === today() ? 'date' : null"
                    [attr.aria-disabled]="outOfRange(day) ? 'true' : null"
                    [attr.aria-label]="longLabel(day)"
                    (click)="select(day)"
                  >
                    {{ dayNumber(day) }}
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      </div>
    </ng-template>
  `,
  styles: `
    ${FIELD_INPUT_STYLES}
    .iam-date {
      display: flex;
      align-items: stretch;
      gap: var(--iam-space-1);
    }
    .iam-date__input {
      flex: 1;
    }
    .iam-date__format {
      margin: var(--iam-space-1) 0 0;
      font-size: var(--iam-font-size-xs);
      color: var(--iam-color-text-muted);
    }
    .iam-calendar {
      padding: var(--iam-space-3);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-radius: var(--iam-radius-md);
      box-shadow: 0 8px 24px rgb(0 0 0 / 0.2);
      font-family: var(--iam-font-family);
    }
    .iam-calendar__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--iam-space-2);
      margin-bottom: var(--iam-space-2);
    }
    .iam-calendar__title {
      margin: 0;
      font-size: var(--iam-font-size-md);
      font-weight: var(--iam-font-weight-semibold);
    }
    .iam-calendar__grid {
      border-collapse: collapse;
    }
    .iam-calendar__grid th {
      padding: var(--iam-space-1);
      font-size: var(--iam-font-size-xs);
      font-weight: var(--iam-font-weight-semibold);
      color: var(--iam-color-text-muted);
    }
    .iam-calendar__day {
      width: 2.25rem;
      height: 2.25rem;
      padding: 0;
      text-align: center;
      border-radius: var(--iam-radius-sm);
      color: var(--iam-color-text);
      cursor: pointer;
    }
    .iam-calendar__day:hover {
      background: var(--iam-color-surface);
    }
    .iam-calendar__day:focus {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: -2px;
    }
    .iam-calendar__day--outside {
      color: var(--iam-color-text-muted);
    }
    .iam-calendar__day--today {
      font-weight: var(--iam-font-weight-semibold);
      text-decoration: underline;
    }
    .iam-calendar__day[aria-selected='true'] {
      background: var(--iam-color-primary);
      color: var(--iam-color-on-primary);
    }
    .iam-calendar__day[aria-disabled='true'] {
      color: var(--iam-color-disabled-text);
      text-decoration: line-through;
      cursor: not-allowed;
    }
  `,
})
export class IamDatePicker extends IamValueControl<IsoDate | null> {
  /** Earliest allowed date (inclusive), yyyy-MM-dd. */
  readonly min = input<IsoDate | null>(null);
  /** Latest allowed date (inclusive), yyyy-MM-dd. */
  readonly max = input<IsoDate | null>(null);
  readonly readonly = input<boolean>(false);
  readonly ariaLabel = input<string | null>(null);
  readonly openLabel = input<string>('Choose date');
  /** Extra ids for aria-describedby (used by iam-date-range). */
  readonly describedByExtra = input<string | null>(null);

  protected override readonly validates = true;
  protected readonly positions = POSITIONS;
  protected readonly addMonths = addMonths;
  protected readonly formatId = iamId('iam-date-format');
  protected readonly captionId = iamId('iam-calendar-caption');
  /** Refreshed each time the calendar opens, so a page left open past midnight highlights the right day. */
  protected readonly today = signal<IsoDate>(todayIso());
  protected readonly open = signal(false);
  protected readonly text = signal('');
  protected readonly focusDate = signal<IsoDate>(this.today());
  /** True while the typed text is not a real date. Read by iam-date-range. */
  readonly hasParseError = signal(false);

  private readonly locale = inject(LOCALE_ID);
  private readonly injector = inject(Injector);
  private readonly dayPrefix = iamId('iam-day');
  private readonly textInput = viewChild.required<ElementRef<HTMLInputElement>>('textInput');
  private readonly toggleButton = viewChild.required('toggleButton', {
    read: ElementRef<HTMLButtonElement>,
  });

  protected readonly pattern = computed(() => patternHint(this.locale));
  private readonly firstDay = firstDayOfWeek(this.locale);
  protected readonly weekdays = computed(() => weekdayNames(this.locale, this.firstDay));
  protected readonly weeks = computed(() => monthGrid(this.focusDate(), this.firstDay));
  protected readonly caption = computed(() => monthLabel(this.focusDate(), this.locale));
  protected readonly dialogLabel = computed(() => `Choose date, ${this.caption()}`);
  /** Validity depends on min/max: re-run it when they change (e.g. an end date limited by a start date). */
  private readonly boundsWatch = effect(() => {
    this.min();
    this.max();
    untracked(() => this.revalidate());
  });

  protected readonly describedBy = computed(
    () =>
      [this.ariaDescribedBy(), this.formatId, this.describedByExtra()].filter(Boolean).join(' ') ||
      null,
  );

  protected dayId(day: IsoDate): string {
    return `${this.dayPrefix}-${day}`;
  }

  protected dayNumber(day: IsoDate): number {
    return toParts(day).day;
  }

  protected inMonth(day: IsoDate): boolean {
    return day.slice(0, 7) === this.focusDate().slice(0, 7);
  }

  protected longLabel(day: IsoDate): string {
    return formatIsoDate(day, this.locale, 'long');
  }

  protected outOfRange(day: IsoDate): boolean {
    const min = this.min();
    const max = this.max();
    return (!!min && day < min) || (!!max && day > max);
  }

  protected onText(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.text.set(text);
    if (text.trim() === '') {
      this.hasParseError.set(false);
      this.commit(null);
    } else {
      const iso = parseUserDate(text, this.locale);
      this.hasParseError.set(iso === null);
      this.commit(iso);
    }

    this.revalidate();
  }

  protected onBlur(): void {
    const value = this.value();
    if (value && !this.hasParseError()) {
      this.text.set(this.display(value));
    }

    this.markTouched();
  }

  protected onInputKeydown(event: KeyboardEvent): void {
    if (event.altKey && event.key === 'ArrowDown') {
      event.preventDefault();
      this.openCalendar();
    }
  }

  protected toggle(): void {
    if (this.open()) {
      this.close(true);
    } else {
      this.openCalendar();
    }
  }

  protected openCalendar(): void {
    if (this.isDisabled() || this.readonly()) {
      return;
    }

    this.today.set(todayIso());
    this.focusDate.set(clampDate(this.value() ?? this.today(), this.min(), this.max()));
    this.open.set(true);
    this.focusDay();
  }

  /** Closes the calendar; `restoreFocus` returns focus to the calendar button. */
  protected close(restoreFocus: boolean): void {
    if (!this.open()) {
      return;
    }

    this.open.set(false);
    if (restoreFocus) {
      this.toggleButton().nativeElement.focus();
    }
  }

  protected select(day: IsoDate): void {
    if (this.outOfRange(day)) {
      return;
    }

    this.hasParseError.set(false);
    this.text.set(this.display(day));
    this.commit(day);
    this.revalidate();
    this.close(false);
    this.textInput().nativeElement.focus();
  }

  protected moveFocus(day: IsoDate): void {
    // Four-digit years only: keeps every date a valid yyyy-MM-dd however far the user pages.
    this.focusDate.set(clampDate(day, CALENDAR_MIN, CALENDAR_MAX));
    this.focusDay();
  }

  protected onCalendarKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    const onDay = target.classList.contains('iam-calendar__day');
    const current = this.focusDate();
    const weekStart = addDays(current, -this.offsetInWeek(current));
    let next: IsoDate | null = null;
    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        this.close(true);
        return;
      case 'ArrowLeft':
        next = onDay ? addDays(current, -1) : null;
        break;
      case 'ArrowRight':
        next = onDay ? addDays(current, 1) : null;
        break;
      case 'ArrowUp':
        next = onDay ? addDays(current, -7) : null;
        break;
      case 'ArrowDown':
        next = onDay ? addDays(current, 7) : null;
        break;
      case 'Home':
        next = onDay ? weekStart : null;
        break;
      case 'End':
        next = onDay ? addDays(weekStart, 6) : null;
        break;
      case 'PageUp':
        next = addMonths(current, event.shiftKey ? -12 : -1);
        break;
      case 'PageDown':
        next = addMonths(current, event.shiftKey ? 12 : 1);
        break;
      case 'Enter':
      case ' ':
        if (onDay) {
          event.preventDefault();
          this.select(current);
        }

        return;
      default:
        return;
    }

    if (next) {
      event.preventDefault();
      this.moveFocus(next);
    }
  }

  protected override validate(control: AbstractControl): ValidationErrors | null {
    if (this.hasParseError()) {
      return { iamDate: `Enter a real date as ${this.pattern()}.` };
    }

    const value = control.value as unknown;
    if (value === null || value === undefined || value === '') {
      return null;
    }

    if (!isIsoDate(value)) {
      return { iamDate: 'The date is not valid.' };
    }

    const min = this.min();
    const max = this.max();
    if (min && (value as IsoDate) < min) {
      return { iamDateMin: `The date must be on or after ${formatIsoDate(min, this.locale)}.` };
    }

    if (max && (value as IsoDate) > max) {
      return { iamDateMax: `The date must be on or before ${formatIsoDate(max, this.locale)}.` };
    }

    return null;
  }

  override writeValue(value: IsoDate | null | undefined): void {
    super.writeValue(value);
    const iso = this.value();
    this.hasParseError.set(false);
    this.text.set(iso ? this.display(iso) : '');
  }

  protected emptyValue(): IsoDate | null {
    return null;
  }

  protected normalize(value: IsoDate | null | undefined): IsoDate | null {
    return isIsoDate(value) ? (value as IsoDate) : null;
  }

  /** The value as the user would type it in their locale, e.g. 08/11/2026 (en-GB). */
  private display(iso: IsoDate): string {
    const { year, month, day } = toParts(iso);
    return new Intl.DateTimeFormat(this.locale, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      timeZone: 'UTC',
      numberingSystem: 'latn',
    }).format(new Date(Date.UTC(year, month - 1, day)));
  }

  private offsetInWeek(day: IsoDate): number {
    const { year, month, day: d } = toParts(day);
    return (new Date(Date.UTC(year, month - 1, d)).getUTCDay() - this.firstDay + 7) % 7;
  }

  /** Focuses the day button for `focusDate` after the calendar has rendered. */
  private focusDay(): void {
    afterNextRender(
      () =>
        this.textInput()
          .nativeElement.ownerDocument.getElementById(this.dayId(this.focusDate()))
          ?.focus(),
      { injector: this.injector },
    );
  }
}
