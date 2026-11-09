// Test-only environment for @iam/ui specs (jsdom). Not part of the public API.

// jsdom has no ResizeObserver; AG Grid and CDK overlay use it for layout only.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  } as unknown as typeof ResizeObserver;
}

// jsdom does not implement scrollIntoView; AG Grid and the dropdown may call it.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => undefined;
}

// AG Grid reports misuse (a feature without its module, an invalid option) through console warnings and errors.
// Turn those into test failures, so a silently ignored grid feature cannot pass.
const gridProblems: string[] = [];
for (const level of ['error', 'warn'] as const) {
  const original = console[level].bind(console);
  console[level] = (...args: unknown[]) => {
    const text = args.map(String).join(' ');
    if (/AG Grid/i.test(text)) {
      gridProblems.push(text);
    }

    original(...args);
  };
}

afterEach(() => {
  const problems = gridProblems.splice(0);
  if (problems.length > 0) {
    throw new Error(`AG Grid reported problems:\n${problems.join('\n')}`);
  }
});

// Overlays (dropdowns, dialogs, tooltips) render into a container on <body> that outlives each test's fixture.
// Empty it after every test, so an overlay left open by a failing test cannot break the tests that follow.
afterEach(() => {
  document
    .querySelectorAll('.cdk-overlay-container')
    .forEach((container) => container.replaceChildren());
});
