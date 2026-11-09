# IAM Platform frontend

Angular 22 workspace (S1-09, ADR-0019). Node 24 (`.nvmrc`).

| Project | What it is |
|---|---|
| `projects/iam-ui` | First-party UI library `@iam/ui`: design tokens, form controls, dialogs, feedback. Public API: `src/public-api.ts`. |
| `projects/iam-ui/grid` | `@iam/ui/grid`: the AG Grid Community wrapper, the only code allowed to import AG Grid. |
| `projects/iam-portal` | The app. In S1-09 a component playground; IAM feature screens stay blocked until the UI foundation gate is approved. |

## Commands

| Command | Does |
|---|---|
| `npm ci --ignore-scripts` | Install exactly what the lock file says, without package install scripts (none are needed). |
| `npm start` | Serve the portal at http://localhost:4200. |
| `npm test` | Library and portal tests (Vitest, jsdom, axe-core). |
| `npm run lint` | ESLint, including the security and architecture gates. |
| `npm run lint:selftest` | Proves each gate still fires. |
| `npm run format:check` | Prettier. |
| `npm run build` | Library, then the portal with bundle size budgets. |

## Rules (enforced by ESLint)

- Import the library only through `@iam/ui` and `@iam/ui/grid`.
- No other UI component libraries (Kendo, PrimeNG, Clarity, Angular Material, …); AG Grid only through `@iam/ui/grid`.
- No `[innerHTML]`, `bypassSecurityTrust*`, `innerHTML`/`outerHTML` writes, `eval`, `new Function`, `localStorage` or `sessionStorage`.
- The UI is presentation only: the API validates and authorizes every request again.
