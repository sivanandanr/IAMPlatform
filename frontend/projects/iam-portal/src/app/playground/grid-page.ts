import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { IamButton } from '@iam/ui';
import {
  IAM_GRID_PAGE_SIZES,
  IamDataGrid,
  IamGridColumn,
  IamGridExport,
  IamGridExportQuery,
  IamGridFilter,
  IamGridQuery,
  toCsvRow,
} from '@iam/ui/grid';

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
  // Row 0100 has a formula-looking owner (synthetic): the export must neutralize it, and the grid shows it as text.
  owner:
    i === 99 ? '=HYPERLINK("https://example.invalid","Payroll office")' : OWNERS[i % OWNERS.length],
}));

/** The fake server's own allowlist; the real API validates again (BACKEND_INPUT_VALIDATION). */
const SERVER_SORTABLE = new Set(['code', 'name', 'application']);
const SERVER_FILTERS = new Set(['name', 'application']);
/** The fake server's export cap; a real export endpoint caps, authorizes and audits. */
const EXPORT_ROW_CAP = 100;
const CRLF = String.fromCharCode(13, 10);

@Component({
  selector: 'iam-grid-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IamDataGrid, IamButton],
  template: `
    <h1 class="mt-0 text-2xl font-semibold">Data grid</h1>
    <p class="text-iam-muted">
      Server-side paging, sorting, filtering and CSV export against a fake in-memory API. Owner is
      not sortable. The fake export is capped at {{ exportCap }} rows and neutralizes formula cells.
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
      [filters]="filters"
      [exportAction]="exportCsv"
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

  protected readonly filters: IamGridFilter<EntitlementRow>[] = [
    { field: 'name', label: 'Entitlement contains', type: 'text', maxLength: 50 },
    {
      field: 'application',
      label: 'Application',
      type: 'options',
      options: APPLICATIONS.map((a) => ({ value: a, label: a })),
    },
  ];
  protected readonly exportCap = EXPORT_ROW_CAP;

  /** Stands in for the export endpoint: the server filters, sorts, caps and writes the CSV. */
  protected readonly exportCsv = async (
    query: IamGridExportQuery,
    signal: AbortSignal,
  ): Promise<IamGridExport> => {
    await new Promise((resolve) => setTimeout(resolve, 200));
    signal.throwIfAborted();
    const rows = serverRows(query).slice(0, EXPORT_ROW_CAP);
    const lines = [
      toCsvRow(['Code', 'Entitlement', 'Application', 'Owner']),
      ...rows.map((r) => toCsvRow([r.code, r.name, r.application, r.owner])),
    ];
    return {
      fileName: 'entitlements.csv',
      content: new Blob([lines.join(CRLF)], { type: 'text/csv;charset=utf-8' }),
    };
  };

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

/** Filters (allowlisted fields only, contains / equals) and sorts, as the API would. */
function serverRows(query: IamGridExportQuery): EntitlementRow[] {
  const sort = query.sort && SERVER_SORTABLE.has(query.sort.field) ? query.sort : null;
  const filters = Object.entries(query.filters ?? {}).filter(([field]) =>
    SERVER_FILTERS.has(field),
  );
  const sorted = DATA.filter((row) =>
    filters.every(([field, value]) =>
      field === 'application'
        ? row.application === value
        : row.name.toLowerCase().includes(value.slice(0, 50).toLowerCase()),
    ),
  );
  if (sort) {
    const key = sort.field as keyof EntitlementRow;
    sorted.sort(
      (a, b) =>
        a[key].localeCompare(b[key], 'en', { numeric: false }) *
        (sort.direction === 'asc' ? 1 : -1),
    );
  }

  return sorted;
}

function fakeServerPage(query: IamGridQuery): { rows: EntitlementRow[]; total: number } {
  const pageSize = IAM_GRID_PAGE_SIZES.includes(query.pageSize) ? query.pageSize : 10;
  const rows = serverRows(query);
  const start = (Math.max(1, query.page) - 1) * pageSize;
  return { rows: rows.slice(start, start + pageSize), total: rows.length };
}
