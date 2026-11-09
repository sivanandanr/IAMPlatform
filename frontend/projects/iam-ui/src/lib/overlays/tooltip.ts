import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { ComponentPortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Directive,
  ElementRef,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { iamId } from '../forms/field';
import { plainText } from '../feedback/plain-text';

const SHOW_DELAY_MS = 300;
const HIDE_DELAY_MS = 150;
const MAX_TOOLTIP = 200;

@Component({
  selector: 'iam-tooltip-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'tooltip',
    '[id]': 'id()',
    '(mouseenter)': 'hover.set(true)',
    '(mouseleave)': 'hover.set(false)',
  },
  template: `{{ text() }}`,
  styles: `
    :host {
      display: block;
      max-width: 20rem;
      padding: var(--iam-space-1) var(--iam-space-2);
      background: var(--iam-color-text);
      color: var(--iam-color-bg);
      border-radius: var(--iam-radius-sm);
      font-family: var(--iam-font-family);
      font-size: var(--iam-font-size-sm);
      line-height: var(--iam-line-height);
    }
  `,
})
export class IamTooltipPanel {
  readonly id = signal('');
  readonly text = signal('');
  /** The pointer is over the tooltip itself: keep it open (WCAG 1.4.13 hoverable). */
  readonly hover = signal(false);
}

/**
 * Short, supplementary text for a control (S1-10). Shown on hover and on keyboard focus, dismissed with Escape, kept
 * open while the pointer is over it, and linked with aria-describedby. Never put essential information or actions in
 * a tooltip: it is not available on touch and is easy to miss. Text only.
 *
 * Only on native buttons and links, so the description lands on the element that gets focus (not on a custom
 * element wrapping an input). The directive manages the host's aria-describedby: do not also bind
 * [attr.aria-describedby] on the same element.
 *
 * ```html
 * <button iamIconButton label="Refresh" iamTooltip="Reload the list from the server">…</button>
 * ```
 */
@Directive({
  selector: 'button[iamTooltip], a[iamTooltip]',
  host: {
    '(mouseenter)': 'scheduleShow()',
    '(mouseleave)': 'scheduleHide()',
    '(focusin)': 'show()',
    '(focusout)': 'hide()',
    '(keydown.escape)': 'hide()',
  },
})
export class IamTooltip {
  readonly iamTooltip = input.required<string>();

  private readonly overlay = inject(Overlay);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly id = iamId('iam-tooltip');
  private overlayRef: OverlayRef | null = null;
  private panel: IamTooltipPanel | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly visible = signal(false);
  private readonly onDocumentKeydown = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      this.hide();
    }
  };

  constructor() {
    effect(() => {
      const text = plainText(this.iamTooltip(), MAX_TOOLTIP);
      this.panel?.text.set(text);
    });
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.timer);
      this.detach();
    });
  }

  scheduleShow(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.show(), SHOW_DELAY_MS);
  }

  scheduleHide(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      // Persistent while the pointer is over the tooltip or the host still has keyboard focus (WCAG 1.4.13).
      const element = this.host.nativeElement;
      const focused = element.contains(element.ownerDocument.activeElement);
      if (!this.panel?.hover() && !focused) {
        this.hide();
      }
    }, HIDE_DELAY_MS);
  }

  show(): void {
    clearTimeout(this.timer);
    const text = plainText(this.iamTooltip(), MAX_TOOLTIP);
    if (this.visible() || !text) {
      return;
    }

    const element = this.host.nativeElement;
    this.overlayRef = this.overlay.create({
      positionStrategy: this.overlay
        .position()
        .flexibleConnectedTo(element)
        .withPositions([
          {
            originX: 'center',
            originY: 'top',
            overlayX: 'center',
            overlayY: 'bottom',
            offsetY: -6,
          },
          { originX: 'center', originY: 'bottom', overlayX: 'center', overlayY: 'top', offsetY: 6 },
        ]),
      scrollStrategy: this.overlay.scrollStrategies.reposition(),
    });
    const ref = this.overlayRef.attach(new ComponentPortal(IamTooltipPanel));
    this.panel = ref.instance;
    this.panel.id.set(this.id);
    this.panel.text.set(text);
    ref.location.nativeElement.addEventListener('mouseleave', () => this.scheduleHide());
    this.addDescribedBy(element);
    element.ownerDocument.addEventListener('keydown', this.onDocumentKeydown);
    this.visible.set(true);
  }

  hide(): void {
    clearTimeout(this.timer);
    this.detach();
  }

  private detach(): void {
    if (!this.visible()) {
      return;
    }

    const element = this.host.nativeElement;
    element.ownerDocument.removeEventListener('keydown', this.onDocumentKeydown);
    this.removeDescribedBy(element);
    this.overlayRef?.dispose();
    this.overlayRef = null;
    this.panel = null;
    this.visible.set(false);
  }

  private addDescribedBy(element: HTMLElement): void {
    const ids = (element.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    if (!ids.includes(this.id)) {
      element.setAttribute('aria-describedby', [...ids, this.id].join(' '));
    }
  }

  private removeDescribedBy(element: HTMLElement): void {
    const ids = (element.getAttribute('aria-describedby') ?? '')
      .split(/\s+/)
      .filter((id) => id && id !== this.id);
    if (ids.length > 0) {
      element.setAttribute('aria-describedby', ids.join(' '));
    } else {
      element.removeAttribute('aria-describedby');
    }
  }
}
