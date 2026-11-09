// Proves the ESLint security and architecture gates still fire (S1-09, ADR-0019). Each case lints an in-memory file at
// a realistic path and checks which rule reports. Run: npm run lint:selftest (CI runs it after npm run lint).
import { ESLint } from 'eslint';

const eslint = new ESLint();
const portal = 'projects/iam-portal/src/app/selftest.ts';
const control = 'projects/iam-ui/src/lib/controls/selftest.ts';
const grid = 'projects/iam-ui/grid/src/selftest.ts';

const component = (template) => `
import { Component } from '@angular/core';
@Component({ selector: 'iam-selftest', template: \`${template}\` })
export class SelftestComponent { html = ''; }
`;

/** [description, file path, source, expected rule id or null for "no error"] */
const cases = [
  [
    'PrimeNG import is refused',
    portal,
    "import { Button } from 'primeng/button';\nexport const b = Button;\n",
    'no-restricted-imports',
  ],
  [
    'Kendo import is refused',
    portal,
    "import { Grid } from '@progress/kendo-angular-grid';\nexport const g = Grid;\n",
    'no-restricted-imports',
  ],
  [
    'Clarity import is refused',
    portal,
    "import { ClarityModule } from '@clr/angular';\nexport const c = ClarityModule;\n",
    'no-restricted-imports',
  ],
  [
    'Angular Material import is refused',
    portal,
    "import { MatButton } from '@angular/material/button';\nexport const m = MatButton;\n",
    'no-restricted-imports',
  ],
  [
    'AG Grid import outside the wrapper is refused',
    portal,
    "import { themeQuartz } from 'ag-grid-community';\nexport const t = themeQuartz;\n",
    'no-restricted-imports',
  ],
  [
    'AG Grid import in a library control is refused',
    control,
    "import { AgGridAngular } from 'ag-grid-angular';\nexport const a = AgGridAngular;\n",
    'no-restricted-imports',
  ],
  [
    'AG Grid import in @iam/ui/grid is allowed',
    grid,
    "import { themeQuartz } from 'ag-grid-community';\nexport const t = themeQuartz;\n",
    null,
  ],
  [
    'Deep import into the library is refused',
    portal,
    "import { IamButton } from '@iam/ui/src/lib/controls/button';\nexport const b = IamButton;\n",
    'no-restricted-imports',
  ],
  [
    'Relative import into the library is refused',
    portal,
    "import { IamButton } from '../../../iam-ui/src/lib/controls/button';\nexport const b = IamButton;\n",
    'no-restricted-imports',
  ],
  [
    'Public entry points are allowed',
    portal,
    "import { IamButton } from '@iam/ui';\nimport { IamDataGrid } from '@iam/ui/grid';\nexport const x = [IamButton, IamDataGrid];\n",
    null,
  ],
  [
    'bypassSecurityTrust* is refused',
    portal,
    'export function f(s: { bypassSecurityTrustHtml(v: string): unknown }) { return s.bypassSecurityTrustHtml("x"); }\n',
    'no-restricted-syntax',
  ],
  [
    'innerHTML assignment is refused',
    portal,
    'export function f(e: HTMLElement) { e.innerHTML = "<b>x</b>"; }\n',
    'no-restricted-syntax',
  ],
  ['eval is refused', portal, 'export const v = eval("1+1");\n', 'no-restricted-syntax'],
  [
    'new Function is refused',
    portal,
    'export const f = new Function("return 1");\n',
    'no-restricted-syntax',
  ],
  [
    'localStorage is refused',
    portal,
    'export function f() { localStorage.setItem("token", "x"); }\n',
    'no-restricted-globals',
  ],
  [
    'sessionStorage is refused',
    portal,
    'export const s = sessionStorage.length;\n',
    'no-restricted-globals',
  ],
  [
    '[innerHTML] template binding is refused',
    portal,
    component('<div [innerHTML]="html"></div>'),
    'no-restricted-syntax',
  ],
  ['Plain text binding is allowed', portal, component('<p>{{ html }}</p>'), null],
  // Bypass shapes found by the S1-09 security review.
  [
    'innerHTML by bracket access is refused',
    portal,
    "export function f(e: HTMLElement) { e['innerHTML'] = 'x'; }\n",
    'no-restricted-syntax',
  ],
  [
    'outerHTML is refused',
    portal,
    "export function f(e: HTMLElement) { e.outerHTML = 'x'; }\n",
    'no-restricted-syntax',
  ],
  [
    'srcdoc is refused',
    portal,
    "export function f(e: HTMLIFrameElement) { e.srcdoc = 'x'; }\n",
    'no-restricted-syntax',
  ],
  [
    'insertAdjacentHTML is refused',
    portal,
    "export function f(e: HTMLElement) { e.insertAdjacentHTML('beforeend', 'x'); }\n",
    'no-restricted-syntax',
  ],
  [
    'Renderer2.setProperty(innerHTML) is refused',
    portal,
    "export function f(r: { setProperty(e: unknown, n: string, v: unknown): void }, e: unknown) { r.setProperty(e, 'innerHTML', 'x'); }\n",
    'no-restricted-syntax',
  ],
  [
    'setAttribute(onclick) is refused',
    portal,
    "export function f(e: HTMLElement) { e.setAttribute('onclick', 'x()'); }\n",
    'no-restricted-syntax',
  ],
  [
    'Renderer2.setAttribute(srcdoc) is refused',
    portal,
    "export function f(r: { setAttribute(e: unknown, n: string, v: string): void }, e: unknown) { r.setAttribute(e, 'srcdoc', 'x'); }\n",
    'no-restricted-syntax',
  ],
  [
    'DOMParser.parseFromString is refused',
    portal,
    "export const d = new DOMParser().parseFromString('<b>x</b>', 'text/html');\n",
    'no-restricted-syntax',
  ],
  [
    'createContextualFragment is refused',
    portal,
    "export const f = document.createRange().createContextualFragment('<b>x</b>');\n",
    'no-restricted-syntax',
  ],
  [
    'document.write through an alias is refused',
    portal,
    "const d = document;\nexport function f() { d.write('x'); }\n",
    'no-restricted-syntax',
  ],
  [
    'window.eval is refused',
    portal,
    "export const v = window.eval('1');\n",
    'no-restricted-syntax',
  ],
  [
    'Function() without new is refused',
    portal,
    "export const f = Function('return 1');\n",
    'no-restricted-syntax',
  ],
  [
    'setTimeout with a string is refused',
    portal,
    "export function f() { setTimeout('alert(1)', 1); }\n",
    'no-restricted-syntax',
  ],
  [
    'bypassSecurityTrust by bracket access is refused',
    portal,
    "export function f(s: Record<string, (v: string) => unknown>) { return s['bypassSecurityTrustHtml']('x'); }\n",
    'no-restricted-syntax',
  ],
  [
    'window.localStorage is refused',
    portal,
    'export const s = window.localStorage;\n',
    'no-restricted-syntax',
  ],
  [
    "globalThis['sessionStorage'] is refused",
    portal,
    "export const s = globalThis['sessionStorage'];\n",
    'no-restricted-syntax',
  ],
  ['indexedDB is refused', portal, 'export const db = indexedDB;\n', 'no-restricted-globals'],
  [
    'document.cookie is refused',
    portal,
    'export const c = document.cookie;\n',
    'no-restricted-syntax',
  ],
  [
    'Dynamic import of a package is refused',
    portal,
    "export const m = () => import('ag-grid-community');\n",
    'no-restricted-syntax',
  ],
  [
    'Dynamic import with a computed path is refused',
    portal,
    'export const m = (p: string) => import(p);\n',
    'no-restricted-syntax',
  ],
  [
    'Lazy route import of a local file is allowed',
    portal,
    "export const m = () => import('./playground/controls-page');\n",
    null,
  ],
  [
    'require() is refused',
    portal,
    "declare const require: (m: string) => unknown;\nexport const m = require('primeng');\n",
    'no-restricted-syntax',
  ],
  [
    'Deep import into @iam/ui/grid is refused',
    portal,
    "import { IamDataGrid } from '@iam/ui/grid/src/data-grid';\nexport const g = IamDataGrid;\n",
    'no-restricted-imports',
  ],
  [
    'Angular CDK in feature code is refused',
    portal,
    "import { Overlay } from '@angular/cdk/overlay';\nexport const o = Overlay;\n",
    'no-restricted-imports',
  ],
  [
    'Other UI libraries are refused',
    portal,
    "import { GridModule } from '@syncfusion/ej2-angular-grids';\nexport const g = GridModule;\n",
    'no-restricted-imports',
  ],
  [
    '[innerHtml] (any case) binding is refused',
    portal,
    component('<div [innerHtml]="html"></div>'),
    'no-restricted-syntax',
  ],
  [
    '[outerHTML] binding is refused',
    portal,
    component('<div [outerHTML]="html"></div>'),
    'no-restricted-syntax',
  ],
  [
    '[srcdoc] binding is refused',
    portal,
    component('<iframe title="t" [srcdoc]="html"></iframe>'),
    'no-restricted-syntax',
  ],
  [
    '[attr.srcdoc] binding is refused',
    portal,
    component('<iframe title="t" [attr.srcdoc]="html"></iframe>'),
    'no-restricted-syntax',
  ],
  [
    'bind-innerHTML binding is refused',
    portal,
    component('<div bind-innerHTML="html"></div>'),
    'no-restricted-syntax',
  ],
  [
    'Host [innerHTML] binding is refused',
    portal,
    "import { Component } from '@angular/core';\n@Component({ selector: 'iam-x', template: '', host: { '[innerHTML]': 'html' } })\nexport class X { html = ''; }\n",
    'no-restricted-syntax',
  ],
  [
    '@HostBinding(innerHTML) is refused',
    portal,
    "import { Component, HostBinding } from '@angular/core';\n@Component({ selector: 'iam-x', template: '' })\nexport class X { @HostBinding('innerHTML') html = ''; }\n",
    'no-restricted-syntax',
  ],
  [
    '[innerHTML] in an external .html template is refused',
    'projects/iam-portal/src/app/selftest.html',
    '<div [innerHTML]="html"></div>\n',
    'no-restricted-syntax',
  ],
  // S1-10: feature code uses the shared controls, never raw form elements or unstyled buttons.
  [
    'Raw <input> in a portal template is refused',
    portal,
    component('<input aria-label="x" />'),
    'no-restricted-syntax',
  ],
  [
    'Raw <select> in a portal template is refused',
    portal,
    component('<select aria-label="x"></select>'),
    'no-restricted-syntax',
  ],
  [
    'Raw <textarea> in a portal template is refused',
    portal,
    component('<textarea aria-label="x"></textarea>'),
    'no-restricted-syntax',
  ],
  [
    'Plain <button> in a portal template is refused',
    portal,
    component('<button type="button">Go</button>'),
    'no-restricted-syntax',
  ],
  [
    'Raw <input> in an external portal .html template is refused',
    'projects/iam-portal/src/app/selftest.html',
    '<input aria-label="x" />\n',
    'no-restricted-syntax',
  ],
  [
    'Upper-case <INPUT> and <BUTTON> are refused too',
    portal,
    component('<INPUT aria-label="x" /><BUTTON type="button">Go</BUTTON>'),
    'no-restricted-syntax',
  ],
  [
    '<button iamButton> is allowed',
    portal,
    component('<button iamButton type="button">Go</button>'),
    null,
  ],
  [
    '<button iamIconButton> is allowed',
    portal,
    component(
      '<button iamIconButton label="Close" aria-label="Close"><svg aria-hidden="true"></svg></button>',
    ),
    null,
  ],
  ['<iam-textbox> is allowed', portal, component('<iam-textbox ariaLabel="Name" />'), null],
  [
    'The shared library may use raw inputs inside its controls',
    control,
    component('<input aria-label="x" />'),
    null,
  ],
];

let failed = 0;
for (const [description, filePath, source, expectedRule] of cases) {
  const [result] = await eslint.lintText(source, { filePath });
  const errors = result.messages.filter((m) => m.severity === 2);
  const rules = [...new Set(errors.map((m) => m.ruleId ?? `fatal: ${m.message}`))];
  const ok = expectedRule === null ? errors.length === 0 : rules.includes(expectedRule);
  if (!ok) {
    failed += 1;
  }

  console.log(
    `${ok ? 'ok  ' : 'FAIL'} ${description}${ok ? '' : ` (expected ${expectedRule ?? 'no errors'}, got ${rules.join(', ') || 'no errors'})`}`,
  );
}

console.log(`\n${cases.length - failed}/${cases.length} lint gate checks passed`);
process.exit(failed === 0 ? 0 : 1);
