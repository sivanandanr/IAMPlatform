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
  output,
  signal,
  viewChild,
} from '@angular/core';
import { iamId } from '../forms/field';
import { IamValueControl } from '../forms/value-control';
import { IamIconButton } from './button';
import { IamOption } from './choice';
import { FIELD_INPUT_STYLES } from './textbox';

/** Longest search text sent to the parent (the server must cap again). */
export const IAM_DROPDOWN_MAX_QUERY = 100;
const SEARCH_DEBOUNCE_MS = 250;
const TYPEAHEAD_RESET_MS = 500;

const POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
];

/**
 * Single-select dropdown (WAI-ARIA "select-only combobox"; with `searchable`, a searchbox inside the popup).
 * Keyboard: Enter/Space/ArrowDown open; ArrowUp/ArrowDown/Home/End move; typing jumps to a matching option; Enter
 * selects; Escape and Tab close. Options render as text. For server-side options set `filterLocally` to false and
 * react to `searchChange` (debounced, capped at 100 characters); set `loading` and `loadError` meanwhile.
 * A selected label is presentation only: the server re-checks the value and the caller's right to use it.
 */
@Component({
  selector: 'iam-dropdown',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin, IamIconButton],
  template: `
    <div class="iam-dropdown">
      <div
        #trigger
        cdkOverlayOrigin
        #origin="cdkOverlayOrigin"
        class="iam-input iam-dropdown__trigger"
        role="combobox"
        aria-haspopup="listbox"
        [id]="inputId()"
        [attr.tabindex]="isDisabled() ? -1 : 0"
        [attr.aria-expanded]="open() ? 'true' : 'false'"
        [attr.aria-controls]="open() ? listboxId : null"
        [attr.aria-activedescendant]="open() && !searchable() ? activeOptionId() : null"
        [attr.aria-labelledby]="field ? field.labelId : null"
        [attr.aria-label]="field ? null : ariaLabel()"
        [attr.aria-describedby]="ariaDescribedBy()"
        [attr.aria-invalid]="ariaInvalid()"
        [attr.aria-required]="ariaRequired()"
        [attr.aria-disabled]="isDisabled() ? 'true' : null"
        [class.iam-dropdown__trigger--disabled]="isDisabled()"
        (click)="toggle()"
        (keydown)="onTriggerKeydown($event)"
        (blur)="onTriggerBlur()"
      >
        <span
          class="iam-dropdown__value"
          [class.iam-dropdown__placeholder]="selectedLabel() === null"
        >
          {{ selectedLabel() ?? placeholder() }}
        </span>
        <span class="iam-dropdown__chevron" aria-hidden="true">&#9662;</span>
      </div>
      @if (clearable() && value() !== null && !isDisabled()) {
        <button iamIconButton class="iam-dropdown__clear" [label]="clearLabel()" (click)="clear()">
          <span aria-hidden="true">&#10005;</span>
        </button>
      }
    </div>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayMinWidth]="triggerWidth()"
      (overlayOutsideClick)="close(false)"
      (detach)="close(false)"
    >
      <div class="iam-dropdown__panel">
        @if (searchable()) {
          <input
            #search
            class="iam-input iam-dropdown__search"
            type="search"
            role="searchbox"
            autocomplete="off"
            [attr.maxlength]="maxQuery"
            [attr.aria-label]="searchLabel()"
            [attr.aria-controls]="listboxId"
            [attr.aria-activedescendant]="activeOptionId()"
            [value]="query()"
            (input)="onSearch($event)"
            (keydown)="onSearchKeydown($event)"
          />
        }
        @if (loading()) {
          <p class="iam-dropdown__status" role="status">{{ loadingText() }}</p>
        } @else if (loadError()) {
          <p class="iam-dropdown__status iam-dropdown__status--error" role="alert">
            {{ loadError() }}
          </p>
        } @else if (visibleOptions().length === 0) {
          <p class="iam-dropdown__status" role="status">{{ emptyText() }}</p>
        }
        <ul
          class="iam-dropdown__list"
          role="listbox"
          [id]="listboxId"
          [attr.aria-labelledby]="field ? field.labelId : null"
          [attr.aria-label]="field ? null : ariaLabel()"
        >
          @if (!loading() && !loadError()) {
            @for (option of visibleOptions(); track $index) {
              <!-- WAI-ARIA combobox pattern: options are not focusable; the combobox or searchbox keeps focus and
                   handles the keyboard (aria-activedescendant). The click handler is for pointer users only. -->
              <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events, @angular-eslint/template/interactive-supports-focus -->
              <li
                role="option"
                class="iam-dropdown__option"
                [id]="optionId($index)"
                [attr.aria-selected]="option.value === value() ? 'true' : 'false'"
                [attr.aria-disabled]="option.disabled ? 'true' : null"
                [class.iam-dropdown__option--active]="$index === activeIndex()"
                (mousedown)="$event.preventDefault()"
                (click)="choose(option)"
              >
                {{ option.label }}
              </li>
            }
          }
        </ul>
      </div>
    </ng-template>
  `,
  styles: `
    ${FIELD_INPUT_STYLES}
    .iam-dropdown {
      display: flex;
      align-items: stretch;
      gap: var(--iam-space-1);
    }
    .iam-dropdown__trigger {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--iam-space-2);
      cursor: pointer;
      user-select: none;
    }
    .iam-dropdown__trigger:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
    }
    .iam-dropdown__trigger--disabled {
      background: var(--iam-color-disabled-bg);
      color: var(--iam-color-disabled-text);
      cursor: not-allowed;
    }
    .iam-dropdown__placeholder {
      color: var(--iam-color-text-muted);
    }
    .iam-dropdown__value {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .iam-dropdown__panel {
      box-sizing: border-box;
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-1);
      max-height: 18rem;
      padding: var(--iam-space-1);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-radius: var(--iam-radius-md);
      box-shadow: 0 4px 16px rgb(0 0 0 / 0.18);
      font-family: var(--iam-font-family);
    }
    .iam-dropdown__list {
      margin: 0;
      padding: 0;
      list-style: none;
      overflow-y: auto;
    }
    .iam-dropdown__option {
      padding: var(--iam-space-2) var(--iam-space-3);
      border-radius: var(--iam-radius-sm);
      cursor: pointer;
    }
    .iam-dropdown__option[aria-selected='true'] {
      font-weight: var(--iam-font-weight-semibold);
    }
    .iam-dropdown__option--active {
      background: var(--iam-color-primary);
      color: var(--iam-color-on-primary);
    }
    .iam-dropdown__option[aria-disabled='true'] {
      color: var(--iam-color-disabled-text);
      cursor: not-allowed;
    }
    .iam-dropdown__status {
      margin: 0;
      padding: var(--iam-space-2) var(--iam-space-3);
      font-size: var(--iam-font-size-sm);
      color: var(--iam-color-text-muted);
    }
    .iam-dropdown__status--error {
      color: var(--iam-color-danger);
    }
  `,
})
export class IamDropdown<T = string> extends IamValueControl<T | null> {
  readonly options = input.required<readonly IamOption<T>[]>();
  readonly placeholder = input<string>('Select…');
  readonly searchable = input<boolean>(false);
  /** False when the parent filters on the server through `searchChange`. */
  readonly filterLocally = input<boolean>(true);
  readonly loading = input<boolean>(false);
  /** Safe, user-facing message when options failed to load (plain text). */
  readonly loadError = input<string | null>(null);
  readonly emptyText = input<string>('No matching options');
  readonly loadingText = input<string>('Loading options…');
  readonly searchLabel = input<string>('Search options');
  readonly ariaLabel = input<string | null>(null);
  /** Shown when the current value is not in `options` (for example a server-side list not yet loaded). */
  readonly selectedText = input<string | null>(null);
  /** Shows a button next to the dropdown that clears the selection. */
  readonly clearable = input<boolean>(false);
  readonly clearLabel = input<string>('Clear selection');

  /** Debounced search text for server-side options; trimmed and capped at 100 characters. */
  readonly searchChange = output<string>();

  protected readonly listboxId = iamId('iam-listbox');
  protected readonly positions = POSITIONS;
  protected readonly maxQuery = IAM_DROPDOWN_MAX_QUERY;
  protected readonly open = signal(false);
  protected readonly query = signal('');
  protected readonly activeIndex = signal(-1);
  protected readonly triggerWidth = signal(0);

  private readonly optionPrefix = iamId('iam-option');
  private readonly trigger = viewChild.required<ElementRef<HTMLElement>>('trigger');
  private readonly search = viewChild<ElementRef<HTMLInputElement>>('search');
  private searchTimer: ReturnType<typeof setTimeout> | undefined;
  private typeahead = '';
  private typeaheadTimer: ReturnType<typeof setTimeout> | undefined;

  protected readonly visibleOptions = computed(() => {
    const q = this.query().trim().toLocaleLowerCase();
    return this.filterLocally() && q
      ? this.options().filter((o) => o.label.toLocaleLowerCase().includes(q))
      : this.options();
  });

  protected readonly selectedLabel = computed<string | null>(() => {
    const current = this.value();
    if (current === null) {
      return null;
    }

    // A value missing from the options (e.g. a server-side list not loaded yet) is still shown, never hidden.
    return (
      this.options().find((o) => o.value === current)?.label ??
      this.selectedText() ??
      String(current)
    );
  });

  protected readonly activeOptionId = computed(() =>
    this.activeIndex() >= 0 ? this.optionId(this.activeIndex()) : null,
  );

  private readonly injector = inject(Injector);

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.searchTimer);
      clearTimeout(this.typeaheadTimer);
    });

    // Keep the active option visible while moving through a long list.
    effect(() => {
      const id = this.open() ? this.activeOptionId() : null;
      if (id) {
        afterNextRender(
          () =>
            this.trigger()
              .nativeElement.ownerDocument.getElementById(id)
              ?.scrollIntoView({ block: 'nearest' }),
          { injector: this.injector },
        );
      }
    });
  }

  protected clear(): void {
    this.commit(null);
    this.markTouched();
    this.trigger().nativeElement.focus();
  }

  protected optionId(index: number): string {
    return `${this.optionPrefix}-${index}`;
  }

  protected toggle(): void {
    if (this.open()) {
      this.close(true);
    } else {
      this.openPanel();
    }
  }

  protected openPanel(): void {
    if (this.isDisabled()) {
      return;
    }

    this.triggerWidth.set(this.trigger().nativeElement.getBoundingClientRect().width);
    this.open.set(true);
    const selected = this.visibleOptions().findIndex((o) => o.value === this.value());
    this.activeIndex.set(selected >= 0 ? selected : this.nextEnabled(-1, 1));
    if (this.searchable()) {
      // After the overlay has rendered: the search box does not exist before that.
      afterNextRender(() => this.search()?.nativeElement.focus(), { injector: this.injector });
    }
  }

  /** Closes the panel. Any close counts as the user having visited the field (touched). */
  protected close(restoreFocus: boolean): void {
    if (!this.open()) {
      return;
    }

    this.open.set(false);
    clearTimeout(this.searchTimer);
    if (this.query()) {
      // Reset a server-side filter so the next open starts from the full list.
      this.searchChange.emit('');
    }

    this.query.set('');
    this.markTouched();
    if (restoreFocus) {
      this.trigger().nativeElement.focus();
    }
  }

  protected choose(option: IamOption<T>): void {
    if (option.disabled) {
      return;
    }

    if (option.value !== this.value()) {
      this.commit(option.value);
    }

    this.close(true);
  }

  protected onTriggerBlur(): void {
    if (!this.open()) {
      this.markTouched();
    }
  }

  protected onTriggerKeydown(event: KeyboardEvent): void {
    if (this.isDisabled()) {
      return;
    }

    if (!this.open()) {
      if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(event.key)) {
        event.preventDefault();
        this.openPanel();
      }

      return;
    }

    this.handleListKey(event);
  }

  protected onSearchKeydown(event: KeyboardEvent): void {
    this.handleListKey(event, true);
  }

  protected onSearch(event: Event): void {
    const text = (event.target as HTMLInputElement).value.slice(0, IAM_DROPDOWN_MAX_QUERY);
    this.query.set(text);
    this.activeIndex.set(this.nextEnabled(-1, 1));
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.searchChange.emit(text.trim()), SEARCH_DEBOUNCE_MS);
  }

  protected emptyValue(): T | null {
    return null;
  }

  protected normalize(value: T | null | undefined): T | null {
    return value ?? null;
  }

  private handleListKey(event: KeyboardEvent, fromSearch = false): void {
    const options = this.visibleOptions();
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.activeIndex.set(this.nextEnabled(this.activeIndex(), 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.activeIndex.set(this.nextEnabled(this.activeIndex(), -1));
        break;
      case 'Home':
        if (!fromSearch) {
          event.preventDefault();
          this.activeIndex.set(this.nextEnabled(-1, 1));
        }

        break;
      case 'End':
        if (!fromSearch) {
          event.preventDefault();
          this.activeIndex.set(this.nextEnabled(options.length, -1));
        }

        break;
      case 'Enter':
        event.preventDefault();
        if (this.activeIndex() >= 0 && options[this.activeIndex()]) {
          this.choose(options[this.activeIndex()]);
        }

        break;
      case ' ':
        if (!fromSearch) {
          event.preventDefault();
          if (this.activeIndex() >= 0 && options[this.activeIndex()]) {
            this.choose(options[this.activeIndex()]);
          }
        }

        break;
      case 'Escape':
        event.preventDefault();
        this.close(true);
        break;
      case 'Tab':
        if (fromSearch) {
          // The search box lives in an overlay at the end of the page: return to the trigger, not past it.
          event.preventDefault();
          this.close(true);
        } else {
          this.close(false);
        }

        break;
      default:
        if (
          !fromSearch &&
          event.key.length === 1 &&
          !event.ctrlKey &&
          !event.metaKey &&
          !event.altKey
        ) {
          this.typeAhead(event.key);
        }
    }
  }

  private typeAhead(character: string): void {
    clearTimeout(this.typeaheadTimer);
    this.typeahead += character.toLocaleLowerCase();
    this.typeaheadTimer = setTimeout(() => (this.typeahead = ''), TYPEAHEAD_RESET_MS);
    const options = this.visibleOptions();
    const start = this.activeIndex();
    for (let step = 1; step <= options.length; step++) {
      const i = (start + step) % options.length;
      if (!options[i].disabled && options[i].label.toLocaleLowerCase().startsWith(this.typeahead)) {
        this.activeIndex.set(i);
        return;
      }
    }
  }

  private nextEnabled(from: number, direction: 1 | -1): number {
    const options = this.visibleOptions();
    for (let i = from + direction; i >= 0 && i < options.length; i += direction) {
      if (!options[i].disabled) {
        return i;
      }
    }

    return from >= 0 && from < options.length ? from : -1;
  }
}
