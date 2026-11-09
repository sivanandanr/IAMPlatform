import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  InjectionToken,
  inject,
  input,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { IamButton, IamIconButton } from '../controls/button';
import { iamId } from '../forms/field';

/** Id of the dialog title, wired by IamDialogService into aria-labelledby. */
export const IAM_DIALOG_TITLE_ID = new InjectionToken<string>('IAM_DIALOG_TITLE_ID');

export interface IamDialogOptions<D> {
  readonly data?: D;
  /** 'alertdialog' for confirmations that interrupt the user (destructive actions). */
  readonly role?: 'dialog' | 'alertdialog';
  /** CSS selector of the element to focus first; defaults to the dialog heading (not the close button). */
  readonly initialFocus?: string;
  /** Id of an element inside the dialog that describes it (aria-describedby), e.g. a confirmation message. */
  readonly describedById?: string;
  /** Keep the dialog open on Escape and backdrop click, e.g. while it holds unsaved form data. */
  readonly disableClose?: boolean;
  readonly width?: string;
}

/**
 * Opens modal dialogs through Angular CDK Dialog (focus trap, aria-modal, Escape to close, focus returned to the
 * opener). The opened component should use `iam-dialog` for its layout; the title is the dialog's accessible name.
 */
@Injectable({ providedIn: 'root' })
export class IamDialogService {
  private readonly dialog = inject(Dialog);

  open<C, D = unknown, R = unknown>(
    component: ComponentType<C>,
    options: IamDialogOptions<D> = {},
  ): DialogRef<R, C> {
    const titleId = iamId('iam-dialog-title');
    return this.dialog.open<R, D, C>(component, {
      data: options.data,
      role: options.role ?? 'dialog',
      ariaModal: true,
      ariaLabelledBy: titleId,
      ariaDescribedBy: options.describedById ?? null,
      disableClose: options.disableClose ?? false,
      autoFocus: options.initialFocus ?? 'first-heading',
      restoreFocus: true,
      width: options.width ?? 'min(32rem, calc(100vw - 2rem))',
      maxHeight: 'calc(100vh - 2rem)',
      panelClass: 'iam-dialog-panel',
      backdropClass: 'iam-dialog-backdrop',
      providers: [{ provide: IAM_DIALOG_TITLE_ID, useValue: titleId }],
    });
  }
}

/**
 * Layout for a dialog's content: a heading (the dialog's accessible name), a close button, the body and an actions
 * area (`<div iamDialogActions>`).
 */
@Component({
  selector: 'iam-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IamIconButton],
  template: `
    <header class="iam-dialog__header">
      <h2 class="iam-dialog__title" [id]="titleId">{{ heading() }}</h2>
      @if (closable()) {
        <button iamIconButton [label]="closeLabel()" (click)="ref?.close()">
          <span aria-hidden="true">&#10005;</span>
        </button>
      }
    </header>
    <div class="iam-dialog__body"><ng-content /></div>
    <footer class="iam-dialog__actions"><ng-content select="[iamDialogActions]" /></footer>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-4);
      padding: var(--iam-space-6);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      border-radius: var(--iam-radius-md);
      font-family: var(--iam-font-family);
      box-shadow: 0 12px 40px rgb(0 0 0 / 0.3);
    }
    .iam-dialog__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--iam-space-4);
    }
    .iam-dialog__title {
      margin: 0;
      font-size: var(--iam-font-size-xl);
      font-weight: var(--iam-font-weight-semibold);
    }
    .iam-dialog__body {
      font-size: var(--iam-font-size-md);
      line-height: var(--iam-line-height);
      overflow-y: auto;
    }
    .iam-dialog__actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--iam-space-2);
    }
  `,
})
export class IamDialog {
  readonly heading = input.required<string>();
  readonly closable = input<boolean>(true);
  readonly closeLabel = input<string>('Close dialog');

  protected readonly ref = inject(DialogRef, { optional: true });
  protected readonly titleId =
    inject(IAM_DIALOG_TITLE_ID, { optional: true }) ?? iamId('iam-dialog-title');
}

export interface IamConfirmOptions {
  readonly heading: string;
  /** Plain text; rendered as text, never HTML. */
  readonly message: string;
  readonly confirmLabel?: string;
  readonly cancelLabel?: string;
  /** Destructive actions use the danger style and focus Cancel first. */
  readonly destructive?: boolean;
}

@Component({
  selector: 'iam-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IamDialog, IamButton],
  template: `
    <iam-dialog [heading]="data.heading" [closable]="false">
      <p class="iam-confirm__message" [id]="data.messageId">{{ data.message }}</p>
      <div iamDialogActions>
        <button
          iamButton
          class="iam-confirm__cancel"
          variant="secondary"
          (click)="ref.close(false)"
        >
          {{ data.cancelLabel ?? 'Cancel' }}
        </button>
        <button
          iamButton
          class="iam-confirm__confirm"
          [variant]="data.destructive ? 'danger' : 'primary'"
          (click)="ref.close(true)"
        >
          {{ data.confirmLabel ?? 'Confirm' }}
        </button>
      </div>
    </iam-dialog>
  `,
  styles: `
    .iam-confirm__message {
      margin: 0;
    }
  `,
})
export class IamConfirmDialog {
  protected readonly data = inject<IamConfirmOptions & { readonly messageId: string }>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
}

/** Asks the user to confirm. Resolves true only on an explicit confirm; Escape, backdrop and Cancel resolve false. */
@Injectable({ providedIn: 'root' })
export class IamConfirmService {
  private readonly dialogs = inject(IamDialogService);

  async confirm(options: IamConfirmOptions): Promise<boolean> {
    const messageId = iamId('iam-confirm-message');
    const ref = this.dialogs.open<
      IamConfirmDialog,
      IamConfirmOptions & { messageId: string },
      boolean
    >(IamConfirmDialog, {
      data: { ...options, messageId },
      describedById: messageId,
      role: options.destructive ? 'alertdialog' : 'dialog',
      initialFocus: options.destructive ? '.iam-confirm__cancel' : '.iam-confirm__confirm',
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}
