import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { IamDropdown, IamOption, IamToastOutlet } from '@iam/ui';

type ThemeChoice = 'system' | 'light' | 'dark';

const THEMES: readonly IamOption<ThemeChoice>[] = [
  { value: 'system', label: 'System theme' },
  { value: 'light', label: 'Light theme' },
  { value: 'dark', label: 'Dark theme' },
];

/**
 * S1-09 portal shell: a component playground for the @iam/ui foundation, not an IAM feature screen (feature screens
 * stay blocked until the UI foundation gate is approved).
 */
@Component({
  selector: 'iam-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    ReactiveFormsModule,
    IamDropdown,
    IamToastOutlet,
  ],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <header
      class="flex flex-wrap items-center justify-between gap-4 border-b border-iam-border bg-iam-surface px-4 py-3"
    >
      <p class="m-0 text-lg font-semibold">IAM Platform · UI foundation</p>
      <nav aria-label="Playground pages" class="flex gap-4">
        <a
          routerLink="/"
          routerLinkActive="active"
          [routerLinkActiveOptions]="{ exact: true }"
          ariaCurrentWhenActive="page"
          >Controls</a
        >
        <a routerLink="/grid" routerLinkActive="active" ariaCurrentWhenActive="page">Data grid</a>
      </nav>
      <iam-dropdown
        class="theme-select w-44"
        ariaLabel="Theme"
        [formControl]="themeControl"
        [options]="themes"
      />
    </header>
    <main id="main" tabindex="-1" class="mx-auto max-w-5xl px-4 py-6">
      <router-outlet />
    </main>
    <iam-toast-outlet />
  `,
  styles: `
    .skip-link {
      position: absolute;
      left: -9999px;
    }
    .skip-link:focus {
      left: var(--iam-space-4);
      top: var(--iam-space-2);
      z-index: var(--iam-z-toast);
      padding: var(--iam-space-2);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-primary);
    }
    nav a {
      color: var(--iam-color-primary);
      font-weight: var(--iam-font-weight-semibold);
    }
    nav a.active {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    a:focus-visible,
    main:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
    }
  `,
})
export class App {
  /** In memory only: no browser storage is used by the portal. */
  protected readonly themes = THEMES;
  protected readonly themeControl = new FormControl<ThemeChoice>('system', { nonNullable: true });
  protected readonly theme = toSignal(this.themeControl.valueChanges, {
    initialValue: 'system' as ThemeChoice,
  });
  private readonly document = inject(DOCUMENT);

  constructor() {
    effect(() => {
      const root = this.document.documentElement;
      const theme = this.theme();
      if (theme === 'system') {
        root.removeAttribute('data-theme');
      } else {
        root.setAttribute('data-theme', theme);
      }
    });
  }
}
