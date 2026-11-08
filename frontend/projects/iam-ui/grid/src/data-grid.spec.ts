import { Component, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { a11yViolations, XSS_PAYLOAD } from '../../src/testing/a11y';
import { IamDataGrid, IamGridColumn, IamGridQuery } from './data-grid';

interface Entitlement {
  readonly code: string;
  readonly name: string;
  readonly owner: string;
}

@Component({
  imports: [IamDataGrid],
  template: `
    <iam-data-grid
      caption="Entitlements"
      [columns]="columns"
      [rows]="rows()"
      [totalCount]="total()"
      [query]="query()"
      [loading]="loading()"
      [error]="error()"
      (queryChange)="queries.push($event)"
    />
  `,
})
class Host {
  readonly columns: IamGridColumn<Entitlement>[] = [
    { field: 'code', header: 'Code', sortable: true },
    { field: 'name', header: 'Name', sortable: true },
    { field: 'owner', header: 'Owner' },
  ];
  readonly rows = signal<readonly Entitlement[]>([
    { code: '0100', name: 'Finance read', owner: 'Finance workgroup' },
    { code: '0200', name: XSS_PAYLOAD, owner: 'IT' },
  ]);
  readonly total = signal(45);
  readonly query = signal<IamGridQuery>({ page: 1, pageSize: 10, sort: null });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly queries: IamGridQuery[] = [];
  readonly grid = viewChild.required(IamDataGrid<Entitlement>);
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  await fixture.whenStable();
  await new Promise((r) => setTimeout(r, 50));
  fixture.detectChanges();
  const root: HTMLElement = fixture.nativeElement;
  const button = (text: string) =>
    [...root.querySelectorAll<HTMLButtonElement>('.iam-grid__pager button')].find((b) =>
      b.textContent!.includes(text),
    )!;
  /** Drives the wrapper's sort handler as AG Grid would after a header click. */
  const userSorts = (colId: string, sort: 'asc' | 'desc') =>
    (
      fixture.componentInstance.grid() as unknown as { onSortChanged(e: unknown): void }
    ).onSortChanged({
      source: 'uiColumnSorted',
      api: { getColumnState: () => [{ colId, sort }] },
    });
  return { fixture, root, button, userSorts, host: fixture.componentInstance };
}

describe('iam-data-grid', () => {
  it('shows the server page with a heading, headers and plain-text cells', async () => {
    const { root } = await setup();
    const section = root.querySelector('section.iam-grid')!;
    expect(
      root.querySelector(`#${section.getAttribute('aria-labelledby')}`)!.textContent,
    ).toContain('Entitlements');
    const cells = [...root.querySelectorAll('ag-grid-angular .ag-cell')].map((c) =>
      c.textContent!.trim(),
    );
    expect(cells).toEqual(['0100', 'Finance read', 'Finance workgroup', '0200', XSS_PAYLOAD, 'IT']);
    expect(
      [...root.querySelectorAll('ag-grid-angular .ag-header-cell-text')].map((h) => h.textContent),
    ).toEqual(['Code', 'Name', 'Owner']);
    expect(root.textContent).toContain('0100');
    expect(root.textContent).toContain(XSS_PAYLOAD);
    expect(root.querySelector('ag-grid-angular img')).toBeNull();
    expect((globalThis as Record<string, unknown>)['__iamXss']).toBeUndefined();
  });

  it('pages within range and announces the position', async () => {
    const { fixture, root, button, host } = await setup();
    expect(root.textContent).toContain('1–10 of 45');
    expect(root.querySelector('[role=status]')!.textContent).toContain('page 1 of 5, 45 records');
    expect(button('Previous').disabled).toBe(true);
    button('Next').click();
    expect(host.queries.at(-1)).toEqual({ page: 2, pageSize: 10, sort: null });

    host.query.set({ page: 5, pageSize: 10, sort: null });
    fixture.detectChanges();
    expect(button('Next').disabled).toBe(true);
    expect(root.textContent).toContain('41–45 of 45');
  });

  it('only requests allowlisted page sizes and resets to page 1', async () => {
    const { fixture, root, host } = await setup();
    host.query.set({ page: 3, pageSize: 10, sort: null });
    fixture.detectChanges();
    const select = root.querySelector<HTMLSelectElement>('.iam-grid__size select')!;
    expect([...select.options].map((o) => o.value)).toEqual(['10', '25', '50', '100']);
    select.value = '25';
    select.dispatchEvent(new Event('change'));
    expect(host.queries.at(-1)).toEqual({ page: 1, pageSize: 25, sort: null });

    const extra = document.createElement('option');
    extra.value = '100000';
    select.appendChild(extra);
    select.value = '100000';
    const before = host.queries.length;
    select.dispatchEvent(new Event('change'));
    expect(host.queries.length).toBe(before);
  });

  it('sends only sortable columns as sort keys, from page 1', async () => {
    const { host, userSorts } = await setup();
    userSorts('name', 'desc');
    expect(host.queries.at(-1)).toEqual({
      page: 1,
      pageSize: 10,
      sort: { field: 'name', direction: 'desc' },
    });

    userSorts('owner', 'asc');
    expect(host.queries.at(-1)).toEqual({ page: 1, pageSize: 10, sort: null });

    userSorts('passwordHash', 'asc');
    expect(host.queries.at(-1)!.sort).toBeNull();
  });

  it('announces loading, errors and empty results', async () => {
    const { fixture, root, host } = await setup();
    host.loading.set(true);
    fixture.detectChanges();
    expect(root.querySelector('section')!.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[role=status]')!.textContent).toContain('Loading Entitlements');

    host.loading.set(false);
    host.error.set('Entitlements could not be loaded.');
    fixture.detectChanges();
    expect(root.querySelector('[role=alert]')!.textContent).toContain(
      'Entitlements could not be loaded.',
    );

    host.error.set(null);
    host.rows.set([]);
    host.total.set(0);
    fixture.detectChanges();
    expect(root.textContent).toContain('No records to show.');
    expect(root.textContent).toContain('0 of 0');
  });

  it('has no serious accessibility violations', async () => {
    const { root } = await setup();
    expect(await a11yViolations(root)).toEqual([]);
  });
});
