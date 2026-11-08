// Minimal Node.js declarations for test-only file reading (specs run in Node under Vitest). Avoids adding
// @types/node to the library; nothing here is available to library code.
declare module 'node:fs' {
  export function readFileSync(path: string, encoding: 'utf8'): string;
}

declare module 'node:path' {
  export function resolve(...segments: string[]): string;
}

declare const process: { cwd(): string };
