import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { plainText } from '../feedback/plain-text';
import { iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';
import { IamOption } from './choice';
import { FIELD_INPUT_STYLES } from './textbox';

/** One page of search results. `hasMore` tells the user to refine the search; there is no "show all". */
export interface IamAutocompletePage<T> {
  readonly items: readonly IamOption<T>[];
  readonly hasMore: boolean;
}

/**
 * Looks up options for a query. Called with an AbortSignal that is aborted when a newer query starts or the control
 * is destroyed; pass it to `fetch`/HttpClient so stale requests are cancelled. The server enforces query length,
 * result limits, rate limits and authorization; the control only bounds what it sends and shows.
 */
export type IamAutocompleteSearch<T> = (
  query: string,
  signal: AbortSignal,
) => Promise<IamAutocompletePage<T>>;

/** Longest query sent; most results shown (the rest are summarised as "refine your search"). */
export const IAM_AUTOCOMPLETE_MAX_QUERY = 100;
export const IAM_AUTOCOMPLETE_MAX_RESULTS = 50;
/** Fewest characters that ever start a search (DL-027); a caller's `minChars` can raise it, never lower it. */
export const IAM_AUTOCOMPLETE_MIN_CHARS = 2;
/** Longest label shown; labels are cleaned of control and bidi-override characters (no visual impersonation). */
const MAX_LABEL = 200;

const POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
];

type Phase = 'idle' | 'tooShort' | 'loading' | 'results' | 'error';

/**
 * Editable combobox with server-side lookup (WAI-ARIA combobox with list autocomplete). Searches start at `minChars`
 * (at least 2); "*" or empty text never searches (DL-027: no "show everything"; the server must still treat the text
 * as literal and enforce its own minimum). Requests are debounced, the previous one
 * is aborted, and a late response for an older query is ignored. Only the first page is shown, capped at 50, with a
 * "refine your search" note when more exist. Nothing is highlighted until the user moves to an option, so Enter never
 * picks a result the user did not choose (identity pickers). The text box always matches the selected value: editing after a
 * selection clears the value, and leaving the box restores the selected label. The selected label is presentation only;
 * the server must re-check the value and the caller's right to use it.
 */
@Component({
  selector: 'iam-autocomplete',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin],
  template: `
    <input
      #textInput
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      class="iam-input"
      type="text"
      role="combobox"
      aria-autocomplete="list"
      autocomplete="off"
      [id]="inputId()"
      [value]="text()"
      [attr.maxlength]="maxQuery"
      [attr.placeholder]="placeholder() || null"
      [attr.aria-expanded]="open() ? 'true' : 'false'"
      [attr.aria-controls]="showList() ? listboxId : null"
      [attr.aria-activedescendant]="open() && activeIndex() >= 0 ? optionId(activeIndex()) : null"
      [attr.aria-describedby]="describedBy()"
      [attr.aria-invalid]="ariaInvalid()"
      [attr.aria-required]="ariaRequired()"
      [attr.aria-label]="field ? null : ariaLabel()"
      [disabled]="isDisabled()"
      (input)="onInput($event)"
      (keydown)="onKeydown($event)"
      (blur)="onBlur()"
    />
    <p class="iam-visually-hidden" [id]="statusId" role="status" aria-live="polite">
      {{ statusText() }}
    </p>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayMinWidth]="width()"
      (overlayOutsideClick)="close()"
      (detach)="close()"
    >
      <div class="iam-ac__panel">
        @switch (phase()) {
          @case ('tooShort') {
            <p class="iam-ac__note">{{ tooShortText() }}</p>
          }
          @case ('loading') {
            <p class="iam-ac__note">{{ loadingText() }}</p>
          }
          @case ('error') {
            <p class="iam-ac__note iam-ac__note--error">{{ errorText() }}</p>
          }
          @case ('results') {
            @if (results().length === 0) {
              <p class="iam-ac__note">{{ emptyText() }}</p>
            }
          }
        }
        @if (showList()) {
          <ul
            class="iam-ac__list"
            role="listbox"
            [id]="listboxId"
            [attr.aria-labelledby]="field ? field.labelId : null"
            [attr.aria-label]="field ? null : ariaLabel()"
          >
            @for (option of results(); track $index) {
              <!-- Options are not focusable: the input keeps focus and points at the active one (aria-activedescendant). -->
              <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events, @angular-eslint/template/interactive-supports-focus -->
              <li
                role="option"
                class="iam-ac__option"
                [id]="optionId($index)"
                [attr.aria-selected]="$index === activeIndex() ? 'true' : 'false'"
                [attr.aria-disabled]="option.disabled ? 'true' : null"
                [class.iam-ac__option--active]="$index === activeIndex()"
                (mousedown)="$event.preventDefault()"
                (click)="choose(option)"
              >
                {{ cleanLabel(option.label) }}
              </li>
            }
          </ul>
        }
        @if (phase() === 'results' && hasMore()) {
          <p class="iam-ac__note">{{ moreText() }}</p>
        }
      </div>
    </ng-template>
  `,
  styles: `
    ${FIELD_INPUT_STYLES}
    .iam-ac__panel {
      box-sizing: border-box;
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-1);
      max-height: 20rem;
      padding: var(--iam-space-1);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-radius: var(--iam-radius-md);
      box-shadow: 0 4px 16px rgb(0 0 0 / 0.18);
      font-family: var(--iam-font-family);
    }
    .iam-ac__list {
      margin: 0;
      padding: 0;
      list-style: none;
      overflow-y: auto;
    }
    .iam-ac__option {
      padding: var(--iam-space-2) var(--iam-space-3);
      border-radius: var(--iam-radius-sm);
      cursor: pointer;
    }
    .iam-ac__option--active {
      background: var(--iam-color-primary);
      color: var(--iam-color-on-primary);
    }
    .iam-ac__option[aria-disabled='true'] {
      color: var(--iam-color-disabled-text);
      cursor: not-allowed;
    }
    .iam-ac__note {
      margin: 0;
      padding: var(--iam-space-2) var(--iam-space-3);
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-text-muted);
    }
    .iam-ac__note--error {
      color: var(--iam-color-danger);
    }
  `,
})
export class IamAutocomplete<T = string> extends IamValueControl<T | null> {
  readonly search = input.required<IamAutocompleteSearch<T>>();
  readonly minChars = input<number>(2);
  readonly debounceMs = input<number>(250);
  readonly placeholder = input<string>('');
  readonly ariaLabel = input<string | null>(null);
  /**
   * The option for a value written by the form (e.g. editing an existing record). Its label is shown only while the
   * value equals `selectedOption.value`, so a label can never sit beside a different person's id.
   */
  readonly selectedOption = input<IamOption<T> | null>(null);
  readonly emptyText = input<string>('No matches.');
  readonly loadingText = input<string>('Searching…');
  readonly errorText = input<string>('Search failed. Try again.');
  readonly moreText = input<string>('More results exist. Refine your search.');

  protected readonly positions = POSITIONS;
  protected readonly maxQuery = IAM_AUTOCOMPLETE_MAX_QUERY;
  protected readonly listboxId = iamId('iam-ac-listbox');
  protected readonly statusId = iamId('iam-ac-status');
  protected readonly text = signal('');
  protected readonly open = signal(false);
  protected readonly phase = signal<Phase>('idle');
  protected readonly results = signal<readonly IamOption<T>[]>([]);
  protected readonly hasMore = signal(false);
  protected readonly activeIndex = signal(-1);
  protected readonly width = signal(0);

  private readonly injector = inject(Injector);
  private readonly optionPrefix = iamId('iam-ac-option');
  private readonly textInput = viewChild.required<ElementRef<HTMLInputElement>>('textInput');
  /** Label of the current value, so the text can be restored when the user leaves the box. */
  private selectedLabel: string | null = null;
  /** Where the current label came from: the user's pick, or a value written by the form (labelled by selectedText). */
  private labelSource: 'user' | 'form' | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private controller: AbortController | null = null;
  private requestSeq = 0;

  private readonly effectiveMinChars = computed(() =>
    Math.max(IAM_AUTOCOMPLETE_MIN_CHARS, Math.trunc(this.minChars()) || 0),
  );
  private readonly effectiveDebounce = computed(() =>
    Math.min(2000, Math.max(0, Math.trunc(this.debounceMs()) || 0)),
  );
  protected readonly showList = computed(
    () => this.open() && this.phase() === 'results' && this.results().length > 0,
  );
  protected readonly tooShortText = computed(
    () => `Type at least ${this.effectiveMinChars()} characters to search.`,
  );
  protected readonly describedBy = computed(() =>
    [this.ariaDescribedBy(), this.statusId].filter(Boolean).join(' '),
  );
  protected readonly statusText = computed(() => {
    switch (this.phase()) {
      case 'loading':
        return this.loadingText();
      case 'error':
        return this.errorText();
      case 'results':
        return this.results().length === 0
          ? this.emptyText()
          : `${this.results().length} results${this.hasMore() ? '. ' + this.moreText() : '.'}`;
      default:
        return '';
    }
  });

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.timer);
      this.controller?.abort();
    });
    // An option supplied after the form wrote the value (same change detection pass or later) still applies.
    effect(() => {
      this.selectedOption();
      if (this.labelSource === 'form' && this.value() !== null) {
        this.selectedLabel = this.formLabel(this.value());
        if (!this.open()) {
          this.text.set(this.selectedLabel);
        }
      }
    });
    effect(() => {
      const index = this.open() ? this.activeIndex() : -1;
      if (index >= 0) {
        const id = this.optionId(index);
        afterNextRender(
          () =>
            this.textInput()
              .nativeElement.ownerDocument.getElementById(id)
              ?.scrollIntoView({ block: 'nearest' }),
          { injector: this.injector },
        );
      }
    });
  }

  protected cleanLabel(label: string): string {
    return plainText(label, MAX_LABEL);
  }

  protected optionId(index: number): string {
    return `${this.optionPrefix}-${index}`;
  }

  protected onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value.slice(0, IAM_AUTOCOMPLETE_MAX_QUERY);
    this.text.set(text);
    if (this.value() !== null) {
      // The text no longer matches the selection: there is no value until the user picks again.
      this.selectedLabel = null;
      this.labelSource = null;
      this.commit(null);
    }

    this.schedule(text);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const items = this.results();
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.open()) {
          this.schedule(this.text(), 0);
        } else if (this.phase() === 'results') {
          this.activeIndex.set(this.next(this.activeIndex(), 1));
        }

        break;
      case 'ArrowUp':
        if (this.open() && this.phase() === 'results') {
          event.preventDefault();
          this.activeIndex.set(this.next(this.activeIndex(), -1));
        }

        break;
      case 'Enter':
        // Only an option the user can see and has moved to: never a result from an older query.
        if (this.showList() && this.activeIndex() >= 0 && items[this.activeIndex()]) {
          event.preventDefault();
          this.choose(items[this.activeIndex()]);
        }

        break;
      case 'Escape':
        if (this.open()) {
          event.preventDefault();
          this.close();
        }

        break;
      case 'Tab':
        this.close();
        break;
    }
  }

  protected onBlur(): void {
    this.close();
    // Never leave text that does not match the value.
    this.text.set(this.selectedLabel ?? '');
    this.markTouched();
  }

  protected choose(option: IamOption<T>): void {
    if (option.disabled) {
      return;
    }

    this.selectedLabel = this.cleanLabel(option.label);
    this.labelSource = 'user';
    this.text.set(this.selectedLabel);
    this.commit(option.value);
    this.close();
  }

  protected close(): void {
    clearTimeout(this.timer);
    this.controller?.abort();
    this.controller = null;
    this.open.set(false);
    this.activeIndex.set(-1);
  }

  override writeValue(value: T | null | undefined): void {
    super.writeValue(value);
    this.labelSource = this.value() === null ? null : 'form';
    this.selectedLabel = this.value() === null ? null : this.formLabel(this.value());
    this.text.set(this.selectedLabel ?? '');
  }

  /** Label for a form-written value: the matching selectedOption's label, else a plain string or number value. */
  private formLabel(value: T | null): string {
    const option = this.selectedOption();
    if (option && option.value === value) {
      return this.cleanLabel(option.label);
    }

    return typeof value === 'string' || typeof value === 'number'
      ? plainText(value, MAX_LABEL)
      : '';
  }

  protected emptyValue(): T | null {
    return null;
  }

  protected normalize(value: T | null | undefined): T | null {
    return value ?? null;
  }

  private schedule(text: string, delay = this.effectiveDebounce()): void {
    clearTimeout(this.timer);
    this.controller?.abort();
    this.controller = null;
    // Results of the previous query are gone the moment the text changes: nothing stale can be highlighted or chosen.
    this.results.set([]);
    this.hasMore.set(false);
    this.activeIndex.set(-1);
    const query = text.trim();
    this.width.set(this.textInput().nativeElement.getBoundingClientRect().width);
    if (query.length === 0 || query.length < this.effectiveMinChars() || /^\*+$/.test(query)) {
      this.phase.set('tooShort');
      this.open.set(query.length > 0);
      return;
    }

    this.open.set(true);
    this.phase.set('loading');
    this.timer = setTimeout(() => void this.run(query), delay);
  }

  private async run(query: string): Promise<void> {
    const seq = ++this.requestSeq;
    const controller = new AbortController();
    this.controller = controller;
    try {
      const page = await this.search()(query, controller.signal);
      if (seq !== this.requestSeq || controller.signal.aborted) {
        return;
      }

      const items = (page?.items ?? []).slice(0, IAM_AUTOCOMPLETE_MAX_RESULTS);
      this.results.set(items);
      this.hasMore.set(
        !!page?.hasMore || (page?.items?.length ?? 0) > IAM_AUTOCOMPLETE_MAX_RESULTS,
      );
      this.activeIndex.set(-1);
      this.phase.set('results');
    } catch {
      if (seq === this.requestSeq && !controller.signal.aborted) {
        this.results.set([]);
        this.phase.set('error');
      }
    }
  }

  private next(from: number, direction: 1 | -1): number {
    const items = this.results();
    for (let i = from + direction; i >= 0 && i < items.length; i += direction) {
      if (!items[i].disabled) {
        return i;
      }
    }

    return from >= 0 && from < items.length ? from : -1;
  }
}
