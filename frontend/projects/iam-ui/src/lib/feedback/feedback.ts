import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injectable,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { IamIconButton } from '../controls/button';
import { IAM_SAFE_ID } from '../forms/field';
import { plainText } from './plain-text';

export type IamToastKind = 'info' | 'success' | 'warning' | 'error';

export interface IamToast {
  readonly id: number;
  readonly kind: IamToastKind;
  /** Plain text; rendered as text, never HTML. */
  readonly message: string;
}

const MAX_TOASTS = 5;
const TOAST_KINDS: readonly IamToastKind[] = ['info', 'success', 'warning', 'error'];

/** Over the cap, the oldest info/success toast goes first; warnings and errors go only if nothing else is left. */
function keepWithinCap(list: IamToast[]): IamToast[] {
  const result = [...list];
  while (result.length > MAX_TOASTS) {
    const routine = result.findIndex((t) => t.kind === 'info' || t.kind === 'success');
    result.splice(routine >= 0 ? routine : 0, 1);
  }

  return result;
}
const MAX_TOAST_LENGTH = 300;

interface DismissTimer {
  remaining: number;
  startedAt: number;
  handle: ReturnType<typeof setTimeout> | undefined;
}

/**
 * Short, non-blocking messages. Info and success are announced politely and dismiss themselves; warnings and errors
 * are announced assertively and stay until dismissed. Auto-dismiss pauses while the pointer is over the toasts or focus
 * is inside them (WCAG 2.2.1). Render `<iam-toast-outlet />` once in the app shell.
 */
@Injectable({ providedIn: 'root' })
export class IamToastService {
  private nextId = 0;
  private paused = false;
  private readonly pauseReasons = new Set<IamToastPauseReason>();
  private readonly timers = new Map<number, DismissTimer>();
  private readonly items = signal<readonly IamToast[]>([]);
  readonly toasts = this.items.asReadonly();

  show(message: string, kind: IamToastKind = 'info', durationMs?: number): number {
    const id = ++this.nextId;
    const safeKind: IamToastKind = TOAST_KINDS.includes(kind) ? kind : 'info';
    const text = plainText(message, MAX_TOAST_LENGTH);
    this.items.update((list) => keepWithinCap([...list, { id, kind: safeKind, message: text }]));
    const duration = durationMs ?? (safeKind === 'info' || safeKind === 'success' ? 6000 : 0);
    if (duration > 0) {
      const timer: DismissTimer = { remaining: duration, startedAt: Date.now(), handle: undefined };
      this.timers.set(id, timer);
      if (!this.paused) {
        timer.handle = setTimeout(() => this.dismiss(id), duration);
      }
    }

    return id;
  }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    clearTimeout(timer?.handle);
    this.timers.delete(id);
    this.items.update((list) => list.filter((t) => t.id !== id));
  }

  /**
   * Stops auto-dismiss timers while the pointer is over, or focus is inside, the toasts. Hover and focus are tracked
   * separately: the pointer leaving must not dismiss a toast that still has keyboard focus.
   */
  pause(reason: IamToastPauseReason = 'hover'): void {
    this.pauseReasons.add(reason);
    if (this.paused) {
      return;
    }

    this.paused = true;
    const now = Date.now();
    for (const timer of this.timers.values()) {
      clearTimeout(timer.handle);
      timer.handle = undefined;
      timer.remaining = Math.max(0, timer.remaining - (now - timer.startedAt));
    }
  }

  /** Restarts the timers with the time each toast had left, once neither hover nor focus holds them. */
  resume(reason: IamToastPauseReason = 'hover'): void {
    this.pauseReasons.delete(reason);
    if (!this.paused || this.pauseReasons.size > 0) {
      return;
    }

    this.paused = false;
    const now = Date.now();
    for (const [id, timer] of this.timers) {
      timer.startedAt = now;
      timer.handle = setTimeout(() => this.dismiss(id), timer.remaining);
    }
  }
}

export type IamToastPauseReason = 'hover' | 'focus';

@Component({
  selector: 'iam-toast-outlet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IamIconButton],
  host: {
    '(mouseenter)': "service.pause('hover')",
    '(mouseleave)': "service.resume('hover')",
    '(focusin)': "service.pause('focus')",
    '(focusout)': 'onFocusOut($event)',
  },
  template: `
    <div class="iam-toasts" role="status" aria-live="polite" aria-atomic="false">
      @for (toast of polite(); track toast.id) {
        <div [class]="'iam-toast iam-toast--' + toast.kind">
          <span class="iam-toast__text">{{ toast.message }}</span>
          <button iamIconButton label="Dismiss notification" (click)="dismiss(toast.id)">
            <span aria-hidden="true">&#10005;</span>
          </button>
        </div>
      }
    </div>
    <div
      class="iam-toasts iam-toasts--urgent"
      role="alert"
      aria-live="assertive"
      aria-atomic="false"
    >
      @for (toast of urgent(); track toast.id) {
        <div [class]="'iam-toast iam-toast--' + toast.kind">
          <span class="iam-toast__text">{{ toast.message }}</span>
          <button iamIconButton label="Dismiss notification" (click)="dismiss(toast.id)">
            <span aria-hidden="true">&#10005;</span>
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      position: fixed;
      inset-block-end: var(--iam-space-4);
      inset-inline-end: var(--iam-space-4);
      z-index: var(--iam-z-toast);
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-2);
      max-width: min(28rem, calc(100vw - 2rem));
      font-family: var(--iam-font-family);
    }
    .iam-toasts {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-2);
    }
    .iam-toast {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--iam-space-2);
      padding: var(--iam-space-2) var(--iam-space-2) var(--iam-space-2) var(--iam-space-4);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-inline-start-width: 4px;
      border-radius: var(--iam-radius-md);
      box-shadow: 0 4px 16px rgb(0 0 0 / 0.18);
      font-size: var(--iam-font-size-sm);
    }
    .iam-toast--success {
      border-inline-start-color: var(--iam-color-success);
    }
    .iam-toast--info {
      border-inline-start-color: var(--iam-color-info);
    }
    .iam-toast--warning {
      border-inline-start-color: var(--iam-color-warning);
    }
    .iam-toast--error {
      border-inline-start-color: var(--iam-color-danger);
    }
  `,
})
export class IamToastOutlet {
  protected readonly service = inject(IamToastService);
  protected readonly polite = computed(() =>
    this.service.toasts().filter((t) => t.kind === 'info' || t.kind === 'success'),
  );
  protected readonly urgent = computed(() =>
    this.service.toasts().filter((t) => t.kind === 'warning' || t.kind === 'error'),
  );
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** Focus moving between toasts keeps them paused; only leaving the outlet resumes. */
  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (!next || !this.host.nativeElement.contains(next)) {
      this.service.resume('focus');
    }
  }

  /**
   * Dismisses a toast. If it held keyboard focus, focus moves to the next toast's Dismiss button rather than being
   * dropped (a removed element fires no reliable focusout); with no toasts left the focus pause is released.
   */
  protected dismiss(id: number): void {
    const element = this.host.nativeElement;
    const hadFocus = element.contains(element.ownerDocument.activeElement);
    this.service.dismiss(id);
    if (!hadFocus) {
      return;
    }

    afterNextRender(
      () => {
        const next = element.querySelector<HTMLButtonElement>('.iam-toast button');
        if (next) {
          next.focus();
        } else {
          this.service.resume('focus');
        }
      },
      { injector: this.injector },
    );
  }
}

/** One problem to list in a validation summary. `fieldId` (optional) is the id of the control to move focus to. */
export interface IamFieldError {
  readonly label: string;
  readonly message: string;
  readonly fieldId?: string;
}

/**
 * List of form problems shown after a failed submit. Call `focus()` after submitting so keyboard and screen-reader
 * users land on it; each entry can move focus to its field. Text only.
 */
@Component({
  selector: 'iam-validation-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[hidden]': '!hasProblems()' },
  // Nothing is rendered without problems: an empty alert must never appear, whatever the page's CSS does.
  template: `
    @if (hasProblems()) {
      <div class="iam-summary" role="alert" tabindex="-1">
        @switch (headingLevel()) {
          @case (3) {
            <h3 class="iam-summary__title">{{ heading() }}</h3>
          }
          @case (4) {
            <h4 class="iam-summary__title">{{ heading() }}</h4>
          }
          @default {
            <h2 class="iam-summary__title">{{ heading() }}</h2>
          }
        }
        <ul class="iam-summary__list">
          @for (message of general(); track $index) {
            <li>{{ message }}</li>
          }
          @for (error of errors(); track $index) {
            <li>
              @if (canFocus(error)) {
                <button type="button" class="iam-summary__link" (click)="focusField(error)">
                  {{ error.label }}: {{ error.message }}
                </button>
              } @else {
                {{ error.label }}: {{ error.message }}
              }
            </li>
          }
        </ul>
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      font-family: var(--iam-font-family);
    }
    /* Author display:block would otherwise override the hidden attribute. */
    :host([hidden]) {
      display: none;
    }
    .iam-summary {
      padding: var(--iam-space-4);
      border: 2px solid var(--iam-color-danger);
      border-radius: var(--iam-radius-md);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
    }
    .iam-summary:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
    }
    .iam-summary__title {
      margin: 0 0 var(--iam-space-2);
      font-size: var(--iam-font-size-lg);
      color: var(--iam-color-danger);
    }
    .iam-summary__list {
      margin: 0;
      padding-inline-start: var(--iam-space-6);
    }
    .iam-summary__link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--iam-color-primary);
      font: inherit;
      text-decoration: underline;
      cursor: pointer;
      text-align: start;
    }
    .iam-summary__link:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
    }
  `,
})
export class IamValidationSummary {
  readonly errors = input<readonly IamFieldError[]>([]);
  /** Messages not tied to a field (for example a ProblemDetails title). */
  readonly general = input<readonly string[]>([]);
  readonly heading = input<string>('Please correct the following:');
  /** Heading level that fits the page outline (default 2). */
  readonly headingLevel = input<2 | 3 | 4>(2);

  protected readonly hasProblems = computed(
    () => this.errors().length > 0 || this.general().length > 0,
  );

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** Moves keyboard focus to the summary. */
  /** Moves keyboard focus to the summary once the current errors have rendered. */
  focus(): void {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('.iam-summary')?.focus(),
      { injector: this.injector },
    );
  }

  protected canFocus(error: IamFieldError): boolean {
    return !!error.fieldId && IAM_SAFE_ID.test(error.fieldId);
  }

  protected focusField(error: IamFieldError): void {
    if (this.canFocus(error)) {
      this.host.nativeElement.ownerDocument.getElementById(error.fieldId!)?.focus();
    }
  }
}
