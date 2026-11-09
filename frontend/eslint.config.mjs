// ESLint flat config for the IAM frontend (S1-09, ADR-0019). The restrictions below are security and architecture
// gates, not style: CI fails on any of them, and scripts/lint-selftest.mjs proves each one fires, including the known
// bypass shapes (bracket access, Renderer2, window.*, aliases, dynamic import, host bindings, case variants).
// Lint is a guard rail, not a complete control: scripts/check-dependencies.mjs allowlists every package as well.
import angular from 'angular-eslint';
import tseslint from 'typescript-eslint';

/** Third-party UI control libraries. The dependency allowlist is the complete control; this catches stray imports. */
const FORBIDDEN_UI_LIBRARIES = {
  group: [
    '@progress/*',
    'kendo-*',
    'primeng',
    'primeng/*',
    '@primeng/*',
    'primeicons',
    '@clr/*',
    '@angular/material',
    '@angular/material/*',
    '@ng-bootstrap/*',
    'ngx-bootstrap',
    'ngx-bootstrap/*',
    '@syncfusion/*',
    'devextreme',
    'devextreme/*',
    'devextreme-angular',
    'devextreme-angular/*',
    'ng-zorro-antd',
    'ng-zorro-antd/*',
    '@taiga-ui/*',
    '@ng-select/*',
    '@nebular/*',
    '@swimlane/*',
  ],
  message:
    'Third-party UI control libraries are not allowed; build the control in @iam/ui (ADR-0019).',
};

const AG_GRID = {
  group: ['ag-grid-*', 'ag-grid-*/*', '@ag-grid-*/*'],
  message: 'Only @iam/ui/grid may import AG Grid; use IamDataGrid from @iam/ui/grid (ADR-0019).',
};

/** Feature code builds on @iam/ui, not on CDK primitives (CDK is library infrastructure only). */
const CDK = {
  group: ['@angular/cdk', '@angular/cdk/*'],
  message: 'Angular CDK is infrastructure for @iam/ui only; use the @iam/ui components (ADR-0019).',
};

const LIBRARY_INTERNALS = {
  group: [
    '@iam/ui/*',
    '!@iam/ui/grid',
    '@iam/ui/grid/*',
    '**/iam-ui/src/**',
    '**/iam-ui/grid/**',
    '**/iam-ui/src',
    '**/iam-ui/grid',
  ],
  message: "Import the library only through its public API: '@iam/ui' or '@iam/ui/grid'.",
};

const HTML_SINKS = 'innerHTML|outerHTML|srcdoc';
const STORAGE = 'localStorage|sessionStorage|indexedDB|cookie';
const NO_STORAGE =
  'No browser storage: never keep tokens or personal data in the browser (CLAUDE.md, ADR-0012).';

const DANGEROUS_TS = [
  {
    selector: 'MemberExpression[property.name=/^bypassSecurityTrust/]',
    message:
      'bypassSecurityTrust* disables Angular sanitization and is not allowed (UI_FOUNDATION_FIRST).',
  },
  {
    selector: 'MemberExpression[computed=true][property.value=/^bypassSecurityTrust/]',
    message: 'bypassSecurityTrust* disables Angular sanitization and is not allowed.',
  },
  // Code execution.
  { selector: "CallExpression[callee.name='eval']", message: 'eval is not allowed.' },
  { selector: "CallExpression[callee.property.name='eval']", message: 'eval is not allowed.' },
  {
    selector: "NewExpression[callee.name='Function'], CallExpression[callee.name='Function']",
    message: 'Function() is code execution and is not allowed.',
  },
  {
    selector: "MemberExpression[property.name='Function']",
    message: 'Function() is code execution and is not allowed.',
  },
  {
    selector:
      'CallExpression[callee.name=/^(setTimeout|setInterval)$/][arguments.0.type=/^(Literal|TemplateLiteral)$/]',
    message: 'setTimeout/setInterval with a string is code execution; pass a function.',
  },
  {
    selector:
      'CallExpression[callee.property.name=/^(setTimeout|setInterval)$/][arguments.0.type=/^(Literal|TemplateLiteral)$/]',
    message: 'setTimeout/setInterval with a string is code execution; pass a function.',
  },
  { selector: "CallExpression[callee.name='require']", message: 'require() is not used here.' },
  {
    selector: "ImportExpression[source.type!='Literal']",
    message: 'Dynamic import() must use a fixed path.',
  },
  {
    selector: 'ImportExpression[source.value=/^[^.]/]',
    message: 'Dynamic import() is for lazy-loading local routes only (relative paths).',
  },
  // HTML string sinks, by name, by bracket access, and through Renderer2 / DOM APIs.
  {
    selector: `MemberExpression[property.name=/^(${HTML_SINKS})$/i]`,
    message: 'Writing HTML strings is not allowed; render text with Angular bindings.',
  },
  {
    selector: `MemberExpression[computed=true][property.value=/^(${HTML_SINKS})$/i]`,
    message: 'Writing HTML strings is not allowed; render text with Angular bindings.',
  },
  {
    selector: `CallExpression[callee.property.name='setProperty'][arguments.1.value=/^(${HTML_SINKS})$/i]`,
    message: 'Renderer2.setProperty with an HTML property is an XSS sink and is not allowed.',
  },
  {
    selector: `CallExpression[callee.property.name='setAttribute'][arguments.0.value=/^(on.*|${HTML_SINKS})$/i]`,
    message: 'Setting event-handler or HTML attributes is not allowed.',
  },
  {
    selector: `CallExpression[callee.property.name='setAttribute'][arguments.1.value=/^(on.*|${HTML_SINKS})$/i]`,
    message: 'Setting event-handler or HTML attributes is not allowed.',
  },
  {
    selector: "CallExpression[callee.property.name='insertAdjacentHTML']",
    message: 'insertAdjacentHTML is not allowed.',
  },
  {
    selector: 'CallExpression[callee.property.name=/^(parseFromString|createContextualFragment)$/]',
    message: 'Parsing HTML strings is not allowed.',
  },
  {
    selector: 'CallExpression[callee.property.name=/^write(ln)?$/]',
    message: 'document.write (or any .write/.writeln) is not allowed in frontend code.',
  },
  // HTML through host bindings.
  {
    selector: `Property[key.value=/^\\[(${HTML_SINKS}|attr\\.srcdoc)\\]$/i]`,
    message: 'Host bindings to HTML properties are not allowed.',
  },
  {
    selector: `Decorator CallExpression[callee.name='HostBinding'][arguments.0.value=/^(${HTML_SINKS}|attr\\.srcdoc)$/i]`,
    message: 'Host bindings to HTML properties are not allowed.',
  },
  // Browser storage reached through window, self, globalThis or document.
  { selector: `MemberExpression[property.name=/^(${STORAGE})$/]`, message: NO_STORAGE },
  {
    selector: `MemberExpression[computed=true][property.value=/^(${STORAGE})$/]`,
    message: NO_STORAGE,
  },
];

const BROWSER_STORAGE = ['localStorage', 'sessionStorage', 'indexedDB', 'caches'].map((name) => ({
  name,
  message: NO_STORAGE,
}));

const TEMPLATE_HTML_BINDING = {
  selector: 'BoundAttribute[name=/^(innerhtml|outerhtml|srcdoc)$/i]',
  message: 'Binding HTML is not allowed; bind text instead (UI_FOUNDATION_FIRST).',
};

export default tseslint.config(
  { ignores: ['dist/**', 'out-tsc/**', '.angular/**', 'node_modules/**', 'coverage/**'] },
  {
    files: ['projects/**/*.ts'],
    extends: [
      tseslint.configs.eslintRecommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'iam', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'iam', style: 'camelCase' },
      ],
      'no-restricted-imports': [
        'error',
        { patterns: [FORBIDDEN_UI_LIBRARIES, AG_GRID, CDK, LIBRARY_INTERNALS] },
      ],
      'no-restricted-syntax': ['error', ...DANGEROUS_TS],
      'no-restricted-globals': ['error', ...BROWSER_STORAGE],
    },
  },
  {
    // Inside the library, its own relative imports and CDK are fine; it still may not use other UI libraries.
    files: ['projects/iam-ui/**/*.ts'],
    rules: {
      'no-restricted-imports': ['error', { patterns: [FORBIDDEN_UI_LIBRARIES, AG_GRID] }],
    },
  },
  {
    // Buttons are attribute components on a native <button> by design (button[iamButton], button[iamIconButton]).
    files: ['projects/iam-ui/src/lib/controls/button.ts'],
    rules: { '@angular-eslint/component-selector': 'off' },
  },
  {
    // The grid entry point is the one place AG Grid may be imported.
    files: ['projects/iam-ui/grid/**/*.ts'],
    rules: { 'no-restricted-imports': ['error', { patterns: [FORBIDDEN_UI_LIBRARIES] }] },
  },
  {
    // Specs may read browser storage to prove it stays empty, and import test helpers by path.
    files: ['projects/**/*.spec.ts', 'projects/iam-ui/src/testing/**/*.ts'],
    rules: { 'no-restricted-globals': 'off' },
  },
  {
    files: ['projects/**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
    rules: { 'no-restricted-syntax': ['error', TEMPLATE_HTML_BINDING] },
  },
  {
    // Feature and app templates use @iam/ui controls, never raw form elements or unstyled buttons (ADR-0019).
    files: ['projects/iam-portal/**/*.html'],
    rules: {
      'no-restricted-syntax': [
        'error',
        TEMPLATE_HTML_BINDING,
        {
          selector: 'Element[name=/^(input|select|textarea)$/i]',
          message:
            'Use the @iam/ui controls (iam-textbox, iam-dropdown, iam-textarea, …), not raw form elements.',
        },
        {
          selector: 'Element[name=/^button$/i]:not(:has(TextAttribute[name=/^iam(Icon)?Button$/]))',
          message: 'Buttons use iamButton or iamIconButton from @iam/ui.',
        },
      ],
    },
  },
);
