import {
  ChangeDetectionStrategy,
  Component,
  CSP_NONCE,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
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
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { IamButton, IamDropdown, IamFormField, IamOption, IamTextbox, iamId } from '@iam/ui';
import { safeCsvFileName } from './csv';

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

/** What the grid asks the server for. `page` is 1-based. `filters` holds only allowlisted fields with bounded values. */
export interface IamGridQuery {
  readonly page: number;
  readonly pageSize: number;
  readonly sort: { readonly field: string; readonly direction: IamSortDirection } | null;
  readonly filters?: Readonly<Record<string, string>>;
}

/** A filter the caller allows. Text filters are trimmed and bounded; option filters must be one of `options`. */
export interface IamGridFilter<T> {
  readonly field: Extract<keyof T, string>;
  readonly label: string;
  readonly type: 'text' | 'options';
  readonly options?: readonly IamOption<string>[];
  /** Longest text sent (default 100); the server must bound it too. */
  readonly maxLength?: number;
}

/** A file produced and authorized by the server for an export request. */
export interface IamGridExport {
  readonly fileName: string;
  readonly content: Blob;
}

/** What an export asks for: everything matching the sort and filters. There is deliberately no page or page size. */
export interface IamGridExportQuery {
  readonly sort: IamGridQuery['sort'];
  readonly filters?: Readonly<Record<string, string>>;
}

/**
 * Asks the server for an export of everything matching the query. The server authorizes the caller, re-validates the
 * sort and filters, caps the rows, writes the audit record and neutralizes formula cells (see toCsvField). The signal
 * is aborted if the grid is destroyed; pass it to the HTTP call.
 */
export type IamGridExportAction = (
  query: IamGridExportQuery,
  signal: AbortSignal,
) => Promise<IamGridExport>;

/** How long the object URL lives after the download starts (revoking at once can cancel it in some browsers). */
const REVOKE_AFTER_MS = 1000;

const DEFAULT_FILTER_LENGTH = 100;

function sameFilters(
  a: Readonly<Record<string, string>>,
  b: Readonly<Record<string, string>>,
): boolean {
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every((k) => a[k] === b[k]);
}

/** `filters` is left out of the query when none apply, so "no filters" has one shape. */
function nonEmpty(filters: Record<string, string>): { filters?: Record<string, string> } {
  return Object.keys(filters).length > 0 ? { filters } : {};
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
  imports: [AgGridAngular, IamButton, IamDropdown, IamFormField, IamTextbox, ReactiveFormsModule],
  template: `
    <section
      class="iam-grid"
      [attr.aria-labelledby]="captionId"
      [attr.aria-busy]="loading() ? 'true' : null"
    >
      @switch (headingLevel()) {
        @case (3) {
          <h3 class="iam-grid__caption" [id]="captionId">{{ caption() }}</h3>
        }
        @case (4) {
          <h4 class="iam-grid__caption" [id]="captionId">{{ caption() }}</h4>
        }
        @default {
          <h2 class="iam-grid__caption" [id]="captionId">{{ caption() }}</h2>
        }
      }
      <p class="iam-visually-hidden" role="status" aria-live="polite">{{ statusText() }}</p>

      @if (filters().length > 0) {
        <form
          class="iam-grid__filters"
          [formGroup]="filterForm()"
          [attr.aria-label]="caption() + ' filters'"
          (ngSubmit)="applyFilters()"
        >
          @for (filter of filters(); track filter.field) {
            <iam-form-field [label]="filter.label" [controlId]="filterId(filter.field)">
              @if (filter.type === 'options') {
                <iam-dropdown
                  [formControlName]="filter.field"
                  [options]="filter.options ?? []"
                  [clearable]="true"
                  [clearLabel]="'Clear ' + filter.label"
                  placeholder="Any"
                />
              } @else {
                <iam-textbox
                  [formControlName]="filter.field"
                  [maxLength]="filter.maxLength ?? defaultFilterLength"
                />
              }
            </iam-form-field>
          }
          <div class="iam-grid__filter-actions">
            <button iamButton variant="primary" type="submit" [disabled]="loading()">
              Apply filters
            </button>
            <button iamButton variant="secondary" [disabled]="loading()" (click)="clearFilters()">
              Clear filters
            </button>
          </div>
        </form>
      }

      @if (exportAction()) {
        <div class="iam-grid__toolbar">
          <button
            iamButton
            variant="secondary"
            [loading]="exporting()"
            [disabled]="loading()"
            (click)="exportAll()"
          >
            {{ exportLabel() }}
          </button>
        </div>
        @if (exportError()) {
          <div class="iam-grid__message iam-grid__message--error" role="alert">
            {{ exportError() }}
          </div>
        }
      }

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
    .iam-grid__filters {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: var(--iam-space-3);
    }
    .iam-grid__filters iam-form-field {
      flex: 1 1 12rem;
    }
    .iam-grid__filter-actions,
    .iam-grid__toolbar {
      display: flex;
      gap: var(--iam-space-2);
      justify-content: flex-end;
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
    .iam-grid__size select:focus-visible {
      outline: var(--iam-focus-ring-width) solid var(--iam-color-focus);
      outline-offset: 2px;
    }
  `,
})
export class IamDataGrid<T extends object> {
  /** Accessible name and visible heading of the grid. */
  readonly caption = input.required<string>();
  /** Heading level that fits the page outline (default 2). */
  readonly headingLevel = input<2 | 3 | 4>(2);
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

  /** Filters the caller allows; the filter bar is shown only when there are some. */
  readonly filters = input<readonly IamGridFilter<T>[]>([]);
  /** Server-side export; the Export button is shown only when this is set. */
  readonly exportAction = input<IamGridExportAction | null>(null);
  readonly exportLabel = input<string>('Export CSV');
  readonly exportErrorText = input<string>('The export could not be created. Try again.');

  private readonly document = inject(DOCUMENT);
  protected readonly defaultFilterLength = DEFAULT_FILTER_LENGTH;
  protected readonly exporting = signal(false);
  protected readonly exportError = signal<string | null>(null);
  private readonly filterPrefix = iamId('iam-grid-filter');

  private readonly destroyed = new AbortController();
  /** The filter fields, as one string: the form is rebuilt only when the set of fields changes, not the array. */
  private readonly filterFields = computed(() =>
    this.filters()
      .map((f) => f.field)
      .join('\u0000'),
  );
  /** The filters the caller applied; equal content counts as unchanged, so paging or sorting does not touch them. */
  private readonly appliedFilters = computed(() => this.query().filters ?? {}, {
    equal: sameFilters,
  });

  /** One form control per allowed filter, starting from the current query's values. */
  protected readonly filterForm = computed(() => {
    const fields = this.filterFields().split('\u0000').filter(Boolean);
    return new FormGroup<Record<string, FormControl<string | null>>>(
      Object.fromEntries(
        fields.map((field) => [
          field,
          new FormControl<string | null>(untracked(() => this.appliedFilters()[field] ?? null)),
        ]),
      ),
    );
  });

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
    // Read-only grid: Tab enters it once and leaves it (arrow keys move between cells), instead of stopping on every
    // cell, which would make keyboard users tab through a whole page of data to reach the pager.
    tabToNextCell: () => false,
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
    inject(DestroyRef).onDestroy(() => this.destroyed.abort());

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

  /**
   * Keeps the filter boxes in step when the applied filters change (for example a reset by the caller). Paging and
   * sorting leave the applied filters equal, so text typed but not yet applied is kept.
   */
  private readonly syncFilters = effect(() => {
    const current = this.appliedFilters();
    const form = this.filterForm();
    untracked(() => {
      for (const [field, control] of Object.entries(form.controls)) {
        control.setValue(current[field] ?? null, { emitEvent: false });
      }
    });
  });

  protected filterId(field: string): string {
    return `${this.filterPrefix}-${field}`;
  }

  protected applyFilters(): void {
    const raw = this.filterForm().getRawValue();
    this.emit({ ...this.query(), page: 1, filters: this.cleanFilters(raw) });
  }

  protected clearFilters(): void {
    this.filterForm().reset();
    this.emit({ ...this.query(), page: 1, filters: {} });
  }

  protected async exportAll(): Promise<void> {
    const action = this.exportAction();
    if (!action || this.exporting()) {
      return;
    }

    this.exporting.set(true);
    this.exportError.set(null);
    const signal = this.destroyed.signal;
    try {
      const { sort, filters } = this.query();
      const file = await action(
        {
          // The same allowlist as paging: a sort on a column that is not sortable is not sent.
          sort: sort && this.sortableFields().has(sort.field) ? sort : null,
          ...nonEmpty(this.cleanFilters(filters ?? {})),
        },
        signal,
      );
      if (!signal.aborted) {
        this.download(file);
      }
    } catch {
      if (!signal.aborted) {
        this.exportError.set(this.exportErrorText());
      }
    } finally {
      if (!signal.aborted) {
        this.exporting.set(false);
      }
    }
  }

  /** Only allowlisted filters, text trimmed and bounded, options checked against the allowed values. */
  private cleanFilters(
    raw: Readonly<Record<string, string | null | undefined>>,
  ): Record<string, string> {
    const clean: Record<string, string> = {};
    for (const filter of this.filters()) {
      const value = raw[filter.field];
      if (typeof value !== 'string') {
        continue;
      }

      if (filter.type === 'options') {
        if ((filter.options ?? []).some((o) => o.value === value)) {
          clean[filter.field] = value;
        }
      } else {
        const text = value.trim().slice(0, filter.maxLength ?? DEFAULT_FILTER_LENGTH);
        if (text) {
          clean[filter.field] = text;
        }
      }
    }

    return clean;
  }

  private download(file: IamGridExport): void {
    const doc = this.document;
    // Re-typed as CSV: the server's own Blob type is not trusted to decide how the browser treats the file.
    const url = URL.createObjectURL(new Blob([file.content], { type: 'text/csv;charset=utf-8' }));
    const link = doc.createElement('a');
    link.href = url;
    link.download = safeCsvFileName(file.fileName);
    link.rel = 'noopener';
    doc.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), REVOKE_AFTER_MS);
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
    this.queryChange.emit({
      page,
      pageSize: query.pageSize,
      sort: query.sort,
      ...nonEmpty(this.cleanFilters(query.filters ?? {})),
    });
  }
}
