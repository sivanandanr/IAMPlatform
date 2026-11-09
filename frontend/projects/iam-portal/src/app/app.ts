import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { IamToastOutlet } from '@iam/ui';

type ThemeChoice = 'system' | 'light' | 'dark';

/**
 * S1-09 portal shell: a component playground for the @iam/ui foundation, not an IAM feature screen (feature screens
 * stay blocked until the UI foundation gate is approved).
 */
@Component({
  selector: 'iam-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IamToastOutlet],
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
      <label class="flex items-center gap-2 text-sm">
        Theme
        <select class="theme-select" [value]="theme()" (change)="setTheme($event)">
          <option value="system">System</option>
          <option value="light">Light</option>
          <option value="dark">Dark</option>
        </select>
      </label>
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
    .theme-select:focus-visible,
    main:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: var(--iam-focus-ring-offset);
    }
    .theme-select {
      min-height: 2rem;
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-radius: var(--iam-radius-sm);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      font: inherit;
    }
  `,
})
export class App {
  /** In memory only: no browser storage is used by the portal. */
  protected readonly theme = signal<ThemeChoice>('system');
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

  protected setTheme(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    if (value === 'system' || value === 'light' || value === 'dark') {
      this.theme.set(value);
    }
  }
}
