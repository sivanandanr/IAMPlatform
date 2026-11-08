import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
} from '@angular/core';

export type IamButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost';

const BUTTON_STYLES = `
  :host {
    display: inline-flex; align-items: center; justify-content: center; gap: var(--iam-space-2);
    min-height: var(--iam-control-height); padding: 0 var(--iam-control-padding-x);
    border: var(--iam-border-width) solid transparent; border-radius: var(--iam-radius-md);
    font: inherit; font-family: var(--iam-font-family); font-size: var(--iam-font-size-sm);
    font-weight: var(--iam-font-weight-semibold); line-height: 1; cursor: pointer;
    transition: background-color var(--iam-motion-duration) var(--iam-motion-easing);
  }
  :host(:focus-visible) { outline: var(--iam-focus-ring-width) solid var(--iam-color-focus); outline-offset: var(--iam-focus-ring-offset); }
  :host(.iam-button--primary) { background: var(--iam-color-primary); color: var(--iam-color-on-primary); }
  :host(.iam-button--primary:hover:not(:disabled)) { background: var(--iam-color-primary-hover); }
  :host(.iam-button--secondary) { background: var(--iam-color-surface-raised); color: var(--iam-color-text); border-color: var(--iam-color-border); }
  :host(.iam-button--danger) { background: var(--iam-color-danger); color: var(--iam-color-on-danger); }
  :host(.iam-button--ghost) { background: transparent; color: var(--iam-color-primary); }
  :host(:disabled), :host([aria-disabled='true']) { background: var(--iam-color-disabled-bg); color: var(--iam-color-disabled-text); border-color: var(--iam-color-border-subtle); cursor: not-allowed; }
  .iam-button__spinner { width: 0.875rem; height: 0.875rem; border: 2px solid currentColor; border-right-color: transparent; border-radius: 50%; animation: iam-spin 0.8s linear infinite; }
  @keyframes iam-spin { to { transform: rotate(360deg); } }
  @media (prefers-reduced-motion: reduce) { .iam-button__spinner { animation: none; } }
`;

/**
 * Native `<button>` with IAM styling. `type` defaults to "button" so a button never submits a form by accident;
 * use `type="submit"` explicitly. While `loading`, the button stays focusable but is aria-disabled and ignores clicks.
 *
 * ```html
 * <button iamButton variant="primary" type="submit" [loading]="saving()">Save</button>
 * ```
 */
@Component({
  selector: 'button[iamButton]',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'hostClass()',
    '[attr.type]': 'type()',
    '[attr.aria-disabled]': "loading() ? 'true' : null",
    '[attr.aria-busy]': "loading() ? 'true' : null",
  },
  template: `
    @if (loading()) {
      <span class="iam-button__spinner" aria-hidden="true"></span>
    }
    <ng-content />
  `,
  styles: BUTTON_STYLES,
})
export class IamButton {
  readonly variant = input<IamButtonVariant>('secondary');
  readonly type = input<'button' | 'submit' | 'reset'>('button');
  readonly loading = input<boolean>(false);

  protected readonly hostClass = computed(() => `iam-button iam-button--${this.variant()}`);

  constructor() {
    // Capture phase: runs before any (click) handler the parent template put on this button, so a click while
    // loading never reaches it (a host listener would run too late to stop it).
    const element = inject<ElementRef<HTMLButtonElement>>(ElementRef).nativeElement;
    const block = (event: Event): void => {
      if (this.loading()) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    element.addEventListener('click', block, { capture: true });
    inject(DestroyRef).onDestroy(() =>
      element.removeEventListener('click', block, { capture: true }),
    );
  }
}

/**
 * Icon-only button. `label` is required and becomes the accessible name, because an icon alone has none.
 *
 * ```html
 * <button iamIconButton label="Close dialog" (click)="close()"><svg aria-hidden="true">…</svg></button>
 * ```
 */
@Component({
  selector: 'button[iamIconButton]',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': "'iam-button iam-button--' + variant() + ' iam-icon-button'",
    '[attr.type]': 'type()',
    '[attr.aria-label]': 'label()',
    '[attr.title]': 'label()',
  },
  template: `<ng-content />`,
  styles: `
    ${BUTTON_STYLES} :host {
      padding: 0;
      width: var(--iam-control-height);
    }
  `,
})
export class IamIconButton {
  readonly label = input.required<string>();
  readonly variant = input<IamButtonVariant>('ghost');
  readonly type = input<'button' | 'submit' | 'reset'>('button');
}
