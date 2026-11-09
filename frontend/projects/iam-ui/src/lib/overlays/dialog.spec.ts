import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { a11yViolations, XSS_PAYLOAD } from '../../testing/a11y';
import { IamConfirmService, IamDialog, IamDialogService } from './dialog';

@Component({
  imports: [IamDialog],
  template: `<iam-dialog heading="Request details"
    ><p>Body text</p>
    <div iamDialogActions><button type="button">OK</button></div></iam-dialog
  >`,
})
class DetailsDialog {}

@Component({ template: `<button type="button" id="opener">Open</button>` })
class Opener {
  readonly dialogs = inject(IamDialogService);
  readonly confirmations = inject(IamConfirmService);
}

function setup() {
  const fixture = TestBed.createComponent(Opener);
  fixture.detectChanges();
  return { fixture, opener: fixture.componentInstance };
}

const settle = async (fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) => {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
};

describe('IamDialogService and iam-dialog', () => {
  afterEach(() =>
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => c.replaceChildren()),
  );

  it('opens a modal dialog named by its heading', async () => {
    const { fixture, opener } = setup();
    opener.dialogs.open(DetailsDialog);
    await settle(fixture);
    const dialog = document.querySelector<HTMLElement>('[role=dialog]')!;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const title = dialog.querySelector('h2')!;
    expect(title.textContent).toContain('Request details');
    expect(dialog.getAttribute('aria-labelledby')).toBe(title.id);
    expect(await a11yViolations(document.body)).toEqual([]);
  });

  it('closes on Escape and returns focus to the opener', async () => {
    const { fixture, opener } = setup();
    const button = document.getElementById('opener') as HTMLButtonElement;
    button.focus();
    const ref = opener.dialogs.open(DetailsDialog);
    await settle(fixture);
    let closed = false;
    ref.closed.subscribe(() => (closed = true));
    // CDK recognises Escape by keyCode 27, as a real keyboard event carries.
    document
      .querySelector('[role=dialog]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    await settle(fixture);
    await new Promise((r) => setTimeout(r));
    expect(closed).toBe(true);
    expect(document.querySelector('[role=dialog]')).toBeNull();
    expect(document.activeElement).toBe(button);
  });
});

describe('IamConfirmService', () => {
  afterEach(() =>
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => c.replaceChildren()),
  );

  it('resolves true only on an explicit confirm', async () => {
    const { fixture, opener } = setup();
    const answer = opener.confirmations.confirm({
      heading: 'Submit request?',
      message: 'It goes to your manager.',
    });
    await settle(fixture);
    document.querySelector<HTMLButtonElement>('.iam-confirm__confirm')!.click();
    await settle(fixture);
    expect(await answer).toBe(true);
  });

  it('resolves false on Cancel', async () => {
    const { fixture, opener } = setup();
    const answer = opener.confirmations.confirm({ heading: 'Submit?', message: 'Sure?' });
    await settle(fixture);
    document.querySelector<HTMLButtonElement>('.iam-confirm__cancel')!.click();
    await settle(fixture);
    expect(await answer).toBe(false);
  });

  it('uses an alertdialog with the danger action for destructive confirmations', async () => {
    const { fixture, opener } = setup();
    void opener.confirmations.confirm({
      heading: 'Revoke access?',
      message: 'This cannot be undone.',
      destructive: true,
      confirmLabel: 'Revoke',
    });
    await settle(fixture);
    expect(document.querySelector('[role=alertdialog]')).not.toBeNull();
    const confirm = document.querySelector<HTMLButtonElement>('.iam-confirm__confirm')!;
    expect(confirm.className).toContain('iam-button--danger');
    expect(confirm.textContent).toContain('Revoke');
  });

  it('renders the message as text', async () => {
    const { fixture, opener } = setup();
    void opener.confirmations.confirm({ heading: XSS_PAYLOAD, message: XSS_PAYLOAD });
    await settle(fixture);
    const dialog = document.querySelector('[role=dialog]')!;
    expect(dialog.querySelector('img')).toBeNull();
    expect(dialog.textContent).toContain(XSS_PAYLOAD);
  });
});
