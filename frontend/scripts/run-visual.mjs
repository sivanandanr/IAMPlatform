// Runs the screenshot project (IAM_VISUAL=1) without a cross-platform env package. Extra arguments are passed on,
// e.g. `npm run e2e:visual -- --update-snapshots` after reviewing the changes.
import { spawnSync } from 'node:child_process';

const result = spawnSync(
  'npx',
  ['playwright', 'test', '--project=visual', ...process.argv.slice(2)],
  {
    stdio: 'inherit',
    shell: process.platform === 'win32',
    env: { ...process.env, IAM_VISUAL: '1' },
  },
);
process.exit(result.status ?? 1);
