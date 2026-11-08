import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { IamButton } from '@iam/ui';
import { IAM_GRID_PAGE_SIZES, IamDataGrid, IamGridColumn, IamGridQuery } from '@iam/ui/grid';

interface EntitlementRow {
  readonly code: string;
  readonly name: string;
  readonly application: string;
  readonly owner: string;
}

const APPLICATIONS = ['SAP ECC', 'SuccessFactors', 'Active Directory', 'Payroll', 'Expense'];
const OWNERS = ['Finance workgroup', 'HR operations', 'IT access team', 'Payroll office'];

/** 137 synthetic rows standing in for a server-side list (no real data). */
const DATA: readonly EntitlementRow[] = Array.from({ length: 137 }, (_, i) => ({
  code: String(i + 1).padStart(4, '0'),
  name: `${APPLICATIONS[i % APPLICATIONS.length]} role ${i + 1}`,
  application: APPLICATIONS[i % APPLICATIONS.length],
  owner: OWNERS[i % OWNERS.length],
}));

/** The fake server's own allowlist; the real API validates again (BACKEND_INPUT_VALIDATION). */
const SERVER_SORTABLE = new Set(['code', 'name', 'application']);

@Component({
  selector: 'iam-grid-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IamDataGrid, IamButton],
  template: `
    <h1 class="mt-0 text-2xl font-semibold">Data grid</h1>
    <p class="text-iam-muted">
      Server-side paging and sorting against a fake in-memory API. Owner is not sortable.
    </p>
    <div class="mb-4 flex gap-2">
      <button iamButton (click)="failNext.set(true)">Fail the next request</button>
    </div>
    <iam-data-grid
      caption="Entitlements"
      [columns]="columns"
      [rows]="rows()"
      [totalCount]="total()"
      [query]="query()"
      [loading]="loading()"
      [error]="error()"
      (queryChange)="load($event)"
    />
  `,
})
export class GridPage {
  protected readonly columns: IamGridColumn<EntitlementRow>[] = [
    { field: 'code', header: 'Code', sortable: true, width: 110 },
    { field: 'name', header: 'Entitlement', sortable: true },
    { field: 'application', header: 'Application', sortable: true },
    { field: 'owner', header: 'Owner' },
  ];

  protected readonly query = signal<IamGridQuery>({ page: 1, pageSize: 10, sort: null });
  protected readonly rows = signal<readonly EntitlementRow[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly failNext = signal(false);

  constructor() {
    void this.load(this.query());
  }

  protected async load(query: IamGridQuery): Promise<void> {
    this.query.set(query);
    this.loading.set(true);
    this.error.set(null);
    await new Promise((resolve) => setTimeout(resolve, 200));
    if (this.failNext()) {
      this.failNext.set(false);
      this.loading.set(false);
      this.error.set('Entitlements could not be loaded. Try again.');
      return;
    }

    const page = fakeServerPage(query);
    this.rows.set(page.rows);
    this.total.set(page.total);
    this.loading.set(false);
  }
}

function fakeServerPage(query: IamGridQuery): { rows: EntitlementRow[]; total: number } {
  const pageSize = IAM_GRID_PAGE_SIZES.includes(query.pageSize) ? query.pageSize : 10;
  const sort = query.sort && SERVER_SORTABLE.has(query.sort.field) ? query.sort : null;
  const sorted = [...DATA];
  if (sort) {
    const key = sort.field as keyof EntitlementRow;
    sorted.sort(
      (a, b) =>
        a[key].localeCompare(b[key], 'en', { numeric: false }) *
        (sort.direction === 'asc' ? 1 : -1),
    );
  }

  const start = (Math.max(1, query.page) - 1) * pageSize;
  return { rows: sorted.slice(start, start + pageSize), total: DATA.length };
}
