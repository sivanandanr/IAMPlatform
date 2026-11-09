import {
  ChangeDetectionStrategy,
  Component,
  CSP_NONCE,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { AgGridAngular } from 'ag-grid-angular';
import {
  CellStyleModule,
  ClientSideRowModelModule,
  ColDef,
  ColumnApiModule,
  GridApi,
  GridOptions,
  GridReadyEvent,
  ModuleRegistry,
  SortChangedEvent,
  ValidationModule,
  themeQuartz,
} from 'ag-grid-community';
import { IamButton, iamId } from '@iam/ui';

/**
 * Registers the AG Grid modules on first use, not at import time: a top-level side effect would stop the bundler from
 * tree-shaking AG Grid out of pages that never show a grid. Modules: the client-side row model (one server page at a
 * time), cell styles (alignment classes) and the column-state API (showing the server sort). In development and tests
 * the validation module reports any feature used without its module, instead of failing silently.
 */
let modulesRegistered = false;
function registerGridModules(): void {
  if (!modulesRegistered) {
    ModuleRegistry.registerModules([
      ClientSideRowModelModule,
      CellStyleModule,
      ColumnApiModule,
      ...(typeof ngDevMode === 'undefined' || ngDevMode ? [ValidationModule] : []),
    ]);
    modulesRegistered = true;
  }
}

declare const ngDevMode: boolean | undefined;

export type IamSortDirection = 'asc' | 'desc';

/** A column the caller may show. There is deliberately no renderer hook: cells are always plain text. */
export interface IamGridColumn<T> {
  readonly field: Extract<keyof T, string>;
  readonly header: string;
  /** Only sortable columns can be sent to the server as a sort key (the server checks its own allowlist too). */
  readonly sortable?: boolean;
  readonly width?: number;
  readonly align?: 'start' | 'end';
  /** Formats a value as text. The result is shown as text, never parsed as HTML. */
  readonly format?: (value: unknown, row: T) => string;
}

/** What the grid asks the server for. `page` is 1-based. */
export interface IamGridQuery {
  readonly page: number;
  readonly pageSize: number;
  readonly sort: { readonly field: string; readonly direction: IamSortDirection } | null;
}

/** Page sizes the grid can request (the API caps page size as well). */
export const IAM_GRID_PAGE_SIZES: readonly number[] = [10, 25, 50, 100];

/**
 * Read-only data grid around AG Grid Community (ADR-0019): the only place in the codebase that imports AG Grid.
 * Server-side paging and sorting: the grid never reorders rows itself; it emits `queryChange` with a sort key taken
 * only from columns marked sortable, a page within range and a page size from IAM_GRID_PAGE_SIZES. Loading, error
 * and empty states are announced. Cell values are rendered as text.
 */
@Component({
  selector: 'iam-data-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgGridAngular, IamButton],
  template: `
    <section
      class="iam-grid"
      [attr.aria-labelledby]="captionId"
      [attr.aria-busy]="loading() ? 'true' : null"
    >
      <h2 class="iam-grid__caption" [id]="captionId">{{ caption() }}</h2>
      <p class="iam-visually-hidden" role="status" aria-live="polite">{{ statusText() }}</p>

      @if (error()) {
        <div class="iam-grid__message iam-grid__message--error" role="alert">{{ error() }}</div>
      } @else if (!loading() && rows().length === 0) {
        <div class="iam-grid__message">{{ emptyText() }}</div>
      } @else {
        <ag-grid-angular
          class="iam-grid__table"
          [theme]="theme"
          [rowData]="gridRows()"
          [columnDefs]="columnDefs()"
          [gridOptions]="gridOptions"
          [loading]="loading()"
          (gridReady)="onGridReady($event)"
          (gridPreDestroyed)="api.set(null)"
          (sortChanged)="onSortChanged($event)"
        />
      }

      <nav class="iam-grid__pager" [attr.aria-label]="caption() + ' pages'">
        <label class="iam-grid__size">
          Rows per page
          <select (change)="onPageSize($event)" [disabled]="loading()">
            @for (size of pageSizes; track size) {
              <option [value]="size" [selected]="size === query().pageSize">{{ size }}</option>
            }
          </select>
        </label>
        <span class="iam-grid__range">{{ rangeText() }}</span>
        <button
          iamButton
          variant="secondary"
          [disabled]="loading() || query().page <= 1"
          (click)="goTo(query().page - 1)"
        >
          Previous
        </button>
        <button
          iamButton
          variant="secondary"
          [disabled]="loading() || query().page >= pageCount()"
          (click)="goTo(query().page + 1)"
        >
          Next
        </button>
      </nav>
    </section>
  `,
  styles: `
    :host {
      display: block;
      font-family: var(--iam-font-family);
      color: var(--iam-color-text);
    }
    .iam-grid {
      display: flex;
      flex-direction: column;
      gap: var(--iam-space-2);
    }
    .iam-grid__caption {
      margin: 0;
      font-size: var(--iam-font-size-lg);
    }
    .iam-grid__table {
      width: 100%;
    }
    .iam-grid__message {
      padding: var(--iam-space-6);
      border: var(--iam-border-width) dashed var(--iam-color-border);
      border-radius: var(--iam-radius-md);
      text-align: center;
      color: var(--iam-color-text-muted);
    }
    .iam-grid__message--error {
      border-style: solid;
      border-color: var(--iam-color-danger);
      color: var(--iam-color-danger);
    }
    .iam-grid__pager {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: flex-end;
      gap: var(--iam-space-3);
      font-size: var(--iam-font-size-sm);
    }
    .iam-grid__size select {
      margin-inline-start: var(--iam-space-2);
      min-height: 2rem;
      border: var(--iam-border-width) solid var(--iam-color-border);
      border-radius: var(--iam-radius-sm);
      background: var(--iam-color-surface-raised);
      color: var(--iam-color-text);
      font: inherit;
    }
  `,
})
export class IamDataGrid<T extends object> {
  /** Accessible name and visible heading of the grid. */
  readonly caption = input.required<string>();
  readonly columns = input.required<readonly IamGridColumn<T>[]>();
  /** The current server page of rows. */
  readonly rows = input.required<readonly T[]>();
  readonly totalCount = input.required<number>();
  readonly query = input.required<IamGridQuery>();
  readonly loading = input<boolean>(false);
  /** Safe, user-facing message (plain text), for example from ProblemDetails. */
  readonly error = input<string | null>(null);
  readonly emptyText = input<string>('No records to show.');

  readonly queryChange = output<IamGridQuery>();

  protected readonly captionId = iamId('iam-grid-caption');
  protected readonly pageSizes = IAM_GRID_PAGE_SIZES;
  protected readonly api = signal<GridApi<T> | null>(null);

  protected readonly theme = themeQuartz.withParams({
    fontFamily: 'var(--iam-font-family)',
    fontSize: 14,
    backgroundColor: 'var(--iam-color-surface-raised)',
    foregroundColor: 'var(--iam-color-text)',
    headerBackgroundColor: 'var(--iam-color-surface)',
    headerTextColor: 'var(--iam-color-text)',
    borderColor: 'var(--iam-color-border-subtle)',
    accentColor: 'var(--iam-color-primary)',
    oddRowBackgroundColor: 'var(--iam-color-surface)',
  });

  protected readonly gridOptions: GridOptions<T> = {
    domLayout: 'autoHeight',
    suppressMovableColumns: true,
    suppressMultiSort: true,
    suppressDragLeaveHidesColumns: true,
    // A page has a handful of columns: render them all, so every cell is in the DOM for assistive technology.
    suppressColumnVirtualisation: true,
    enableCellTextSelection: true,
    animateRows: false,
    suppressServerSideFullWidthLoadingRow: true,
    styleNonce: inject(CSP_NONCE, { optional: true }) ?? undefined,
    defaultColDef: {
      resizable: true,
      suppressHeaderMenuButton: true,
      sortingOrder: ['asc', 'desc', null],
    },
    getRowId: undefined,
  };

  /** AG Grid takes a mutable array; the caller's array is never mutated. */
  protected readonly gridRows = computed(() => [...this.rows()]);

  protected readonly sortableFields = computed(
    () =>
      new Set(
        this.columns()
          .filter((c) => c.sortable)
          .map((c) => c.field as string),
      ),
  );

  protected readonly columnDefs = computed<ColDef<T>[]>(() =>
    this.columns().map((column) => ({
      field: column.field as unknown as ColDef<T>['field'],
      headerName: column.header,
      sortable: column.sortable === true,
      // The server has already ordered the page; the grid must never reorder it.
      comparator: () => 0,
      width: column.width,
      // AG Grid's own alignment classes, styled by its theme (component styles cannot reach grid cells).
      cellClass: column.align === 'end' ? 'ag-right-aligned-cell' : undefined,
      headerClass: column.align === 'end' ? 'ag-right-aligned-header' : undefined,
      valueFormatter: (params) => {
        const value: unknown = params.value;
        if (column.format && params.data) {
          return String(column.format(value, params.data));
        }

        return value === null || value === undefined ? '' : String(value);
      },
    })),
  );

  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / this.query().pageSize)),
  );

  protected readonly rangeText = computed(() => {
    const { pageSize } = this.query();
    const total = this.totalCount();
    if (total === 0) {
      return '0 of 0';
    }

    // A page beyond the end (the total shrank) shows the last real page's range, never "41–10 of 10".
    const page = Math.min(this.query().page, this.pageCount());
    const first = (page - 1) * pageSize + 1;
    const last = Math.min(page * pageSize, total);
    return `${first}–${last} of ${total}`;
  });

  protected readonly statusText = computed(() => {
    if (this.loading()) {
      return `Loading ${this.caption()}…`;
    }

    if (this.error()) {
      return '';
    }

    return `${this.caption()}: page ${this.query().page} of ${this.pageCount()}, ${this.totalCount()} records.`;
  });

  constructor() {
    registerGridModules();

    // Show the current server sort on the column headers.
    effect(() => {
      const api = this.api();
      this.query();
      if (api) {
        this.showSort(api);
      }
    });
  }

  private showSort(api: GridApi<T>): void {
    const sort = this.query().sort;
    api.applyColumnState({
      state: sort ? [{ colId: sort.field, sort: sort.direction }] : [],
      defaultState: { sort: null },
    });
  }

  protected onGridReady(event: GridReadyEvent<T>): void {
    this.api.set(event.api);
  }

  protected onSortChanged(event: SortChangedEvent<T>): void {
    if (event.source === 'api') {
      return;
    }

    if (this.loading()) {
      // A request is in flight: put the headers back to the current sort instead of queuing another request.
      this.showSort(event.api);
      return;
    }

    const sorted = event.api.getColumnState().find((c) => c.sort === 'asc' || c.sort === 'desc');
    const sort =
      sorted && this.sortableFields().has(sorted.colId)
        ? { field: sorted.colId, direction: sorted.sort as IamSortDirection }
        : null;
    this.emit({ ...this.query(), page: 1, sort });
  }

  protected goTo(page: number): void {
    this.emit({ ...this.query(), page });
  }

  protected onPageSize(event: Event): void {
    const size = Number((event.target as HTMLSelectElement).value);
    this.emit({ ...this.query(), page: 1, pageSize: size });
  }

  /** Emits only well-formed queries: allowlisted page size and sort field, page within range. */
  private emit(query: IamGridQuery): void {
    if (!IAM_GRID_PAGE_SIZES.includes(query.pageSize)) {
      return;
    }

    if (query.sort && !this.sortableFields().has(query.sort.field)) {
      return;
    }

    const maxPage = Math.max(1, Math.ceil(this.totalCount() / query.pageSize));
    const page = Math.min(Math.max(1, Math.trunc(query.page)), maxPage);
    this.queryChange.emit({ page, pageSize: query.pageSize, sort: query.sort });
  }
}
