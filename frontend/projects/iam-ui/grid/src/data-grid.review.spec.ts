import { Component, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { XSS_PAYLOAD } from '../../src/testing/a11y';
import { IamDataGrid, IamGridColumn, IamGridQuery } from './data-grid';

/** Regression tests for the S1-09 independent review findings on the grid wrapper. */

interface Row {
  readonly code: string;
  readonly amount: number;
}

@Component({
  imports: [IamDataGrid],
  template: `
    <iam-data-grid
      caption="Payments"
      [columns]="columns()"
      [rows]="rows()"
      [totalCount]="total()"
      [query]="query()"
      [loading]="loading()"
      (queryChange)="queries.push($event)"
    />
  `,
})
class Host {
  readonly columns = signal<IamGridColumn<Row>[]>([
    { field: 'code', header: 'Code', sortable: true },
    { field: 'amount', header: 'Amount', sortable: true, align: 'end' },
  ]);
  readonly rows = signal<readonly Row[]>([
    { code: 'A', amount: 10 },
    { code: 'B', amount: 20 },
  ]);
  readonly total = signal(60);
  readonly query = signal<IamGridQuery>({
    page: 1,
    pageSize: 25,
    sort: { field: 'amount', direction: 'desc' },
  });
  readonly loading = signal(false);
  readonly queries: IamGridQuery[] = [];
  readonly grid = viewChild.required(IamDataGrid<Row>);
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  await new Promise((r) => setTimeout(r, 50));
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
  const userSorts = (colId: string, sort: 'asc' | 'desc') =>
    (
      fixture.componentInstance.grid() as unknown as { onSortChanged(e: unknown): void }
    ).onSortChanged({
      source: 'uiColumnSorted',
      api: { getColumnState: () => [{ colId, sort }], applyColumnState: () => true },
    });
  return { fixture, root, host: fixture.componentInstance, userSorts };
}

describe('iam-data-grid (review fixes)', () => {
  it('right-aligns end-aligned columns with AG Grid classes', async () => {
    const { root } = await setup();
    const amountCells = [...root.querySelectorAll('.ag-cell[col-id=amount]')];
    expect(amountCells.length).toBe(2);
    expect(amountCells.every((c) => c.classList.contains('ag-right-aligned-cell'))).toBe(true);
    expect(root.querySelector('.ag-header-cell[col-id=amount]')!.classList).toContain(
      'ag-right-aligned-header',
    );
  });

  it('shows the current server sort on the header', async () => {
    const { root } = await setup();
    expect(root.querySelector('.ag-header-cell[col-id=amount]')!.getAttribute('aria-sort')).toBe(
      'descending',
    );
    expect(root.querySelector('.ag-header-cell[col-id=code]')!.getAttribute('aria-sort')).not.toBe(
      'ascending',
    );
  });

  it('shows the actual page size, not the first option', async () => {
    const { root } = await setup();
    const select = root.querySelector<HTMLSelectElement>('.iam-grid__size select')!;
    expect(select.value).toBe('25');
  });

  it('shows a sensible range when the page is beyond the end', async () => {
    const { fixture, root, host } = await setup();
    host.query.set({ page: 3, pageSize: 25, sort: null });
    host.total.set(10);
    await fixture.whenStable();
    expect(root.querySelector('.iam-grid__range')!.textContent).toContain('1–10 of 10');
  });

  it('ignores sorting while a request is in flight', async () => {
    const { fixture, host, userSorts } = await setup();
    host.loading.set(true);
    await fixture.whenStable();
    userSorts('code', 'asc');
    expect(host.queries).toEqual([]);
  });

  it('renders header text as text', async () => {
    const { fixture, root, host } = await setup();
    host.columns.set([{ field: 'code', header: XSS_PAYLOAD }]);
    await fixture.whenStable();
    await new Promise((r) => setTimeout(r, 50));
    expect(root.querySelector('ag-grid-angular img')).toBeNull();
    expect(root.querySelector('.ag-header-cell-text')!.textContent).toBe(XSS_PAYLOAD);
  });
});
