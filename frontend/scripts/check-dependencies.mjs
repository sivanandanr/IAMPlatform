// Dependency allowlist (S1-09, ADR-0019, DL-018). Every direct dependency must be listed here with its approval, and
// pinned to an exact version. This is the complete control against new UI libraries or other unapproved packages;
// the ESLint import rules only catch stray imports. Adding a package means a reviewed change to this file.
import { readFileSync } from 'node:fs';

/** name -> approval note. Keep in step with DL-018 in docs/architecture/DECISION_LOG.md. */
const APPROVED = {
  // Runtime
  '@angular/cdk': 'DL-018 S1-09: infrastructure only (overlay, dialog)',
  '@angular/common': 'DL-018 S1-09: Angular framework',
  '@angular/compiler': 'DL-018 S1-09: Angular framework',
  '@angular/core': 'DL-018 S1-09: Angular framework',
  '@angular/forms': 'DL-018 S1-09: Angular framework',
  '@angular/platform-browser': 'DL-018 S1-09: Angular framework',
  '@angular/router': 'DL-018 S1-09: Angular framework',
  'ag-grid-angular': 'DL-018 S1-09: AG Grid Community (MIT), only via @iam/ui/grid',
  'ag-grid-community': 'DL-018 S1-09: AG Grid Community (MIT), only via @iam/ui/grid',
  rxjs: 'DL-018 S1-09: Angular dependency',
  tslib: 'DL-018 S1-09: TypeScript helpers',
  // Build, test, lint
  '@angular/build': 'DL-018 S1-09: Angular build',
  '@angular/cli': 'DL-018 S1-09: Angular build',
  '@angular/compiler-cli': 'DL-018 S1-09: Angular build',
  '@playwright/test': 'DL-018 S1-10: real-browser tests (Apache-2.0)',
  '@tailwindcss/postcss': 'DL-018 S1-09: Tailwind',
  'angular-eslint': 'DL-018 S1-09: lint gates',
  'axe-core': 'DL-018 S1-09: accessibility tests',
  eslint: 'DL-018 S1-09: lint gates',
  jsdom: 'DL-018 S1-09: test DOM (Angular default runner)',
  'ng-packagr': 'DL-018 S1-09: library build',
  postcss: 'DL-018 S1-09: required by @tailwindcss/postcss (owner confirmation pending)',
  prettier: 'DL-018 S1-09: formatter from the Angular scaffold (owner confirmation pending)',
  tailwindcss: 'DL-018 S1-09: Tailwind',
  typescript: 'DL-018 S1-09: compiler',
  'typescript-eslint': 'DL-018 S1-09: lint gates',
  vitest: 'DL-018 S1-09: test runner (Angular default)',
};

const EXACT = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/;
const problems = [];

const app = JSON.parse(readFileSync('package.json', 'utf8'));
for (const group of ['dependencies', 'devDependencies', 'optionalDependencies']) {
  for (const [name, version] of Object.entries(app[group] ?? {})) {
    if (!Object.hasOwn(APPROVED, name)) {
      problems.push(
        `${group}: '${name}' is not on the approved list (DL-018); get approval, then add it here.`,
      );
    }

    if (!EXACT.test(version)) {
      problems.push(`${group}: '${name}' must be pinned to an exact version, not '${version}'.`);
    }
  }
}

// The library may only declare peers that the workspace itself is approved to use.
const lib = JSON.parse(readFileSync('projects/iam-ui/package.json', 'utf8'));
for (const group of ['dependencies', 'peerDependencies']) {
  for (const name of Object.keys(lib[group] ?? {})) {
    if (!Object.hasOwn(APPROVED, name)) {
      problems.push(`@iam/ui ${group}: '${name}' is not on the approved list (DL-018).`);
    }
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}

console.log(
  `Dependency allowlist: ${Object.keys(APPROVED).length} approved packages; all direct dependencies approved and pinned.`,
);
