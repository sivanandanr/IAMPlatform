import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { neutralizeCsvCell, safeCsvFileName, toCsvField, toCsvRow } from './csv';
import {
  IamDataGrid,
  IamGridColumn,
  IamGridExportAction,
  IamGridExportQuery,
  IamGridFilter,
  IamGridQuery,
} from './data-grid';

/** S1-10: filter bar, server-side export and the CSV rule the server applies. */

interface Entitlement {
  readonly code: string;
  readonly name: string;
  readonly status: string;
}

@Component({
  imports: [IamDataGrid],
  template: `
    <iam-data-grid
      caption="Entitlements"
      [columns]="columns"
      [rows]="[]"
      [totalCount]="45"
      [query]="query()"
      [filters]="filters()"
      [exportAction]="exportAction()"
      (queryChange)="queries.push($event)"
    />
  `,
})
class Host {
  readonly columns: IamGridColumn<Entitlement>[] = [
    { field: 'code', header: 'Code', sortable: true },
    { field: 'name', header: 'Name' },
  ];
  readonly filters = signal<IamGridFilter<Entitlement>[]>([
    { field: 'name', label: 'Name', type: 'text', maxLength: 10 },
    {
      field: 'status',
      label: 'Status',
      type: 'options',
      options: [
        { value: 'active', label: 'Active' },
        { value: 'retired', label: 'Retired' },
      ],
    },
  ]);
  readonly query = signal<IamGridQuery>({
    page: 3,
    pageSize: 10,
    sort: { field: 'code', direction: 'asc' },
  });
  readonly exportAction = signal<IamGridExportAction | null>(null);
  readonly queries: IamGridQuery[] = [];
}

async function setup() {
  const fixture = TestBed.createComponent(Host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const root: HTMLElement = fixture.nativeElement;
  const host = fixture.componentInstance;
  const button = (text: string) =>
    [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent!.includes(text),
    );
  const typeName = async (value: string) => {
    const input = root.querySelector<HTMLInputElement>('.iam-grid__filters iam-textbox input')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  };
  return { fixture, root, host, button, typeName };
}

describe('iam-data-grid filters', () => {
  it('labels each filter and sends trimmed, bounded values from page 1', async () => {
    const { fixture, root, host, button, typeName } = await setup();
    const form = root.querySelector('form.iam-grid__filters')!;
    expect(form.getAttribute('aria-label')).toBe('Entitlements filters');
    const input = form.querySelector<HTMLInputElement>('iam-textbox input')!;
    expect(root.querySelector(`label[for="${input.id}"]`)!.textContent).toContain('Name');

    await typeName('   Finance read and write   ');
    button('Apply filters')!.click();
    await fixture.whenStable();
    expect(host.queries.at(-1)).toEqual({
      page: 1,
      pageSize: 10,
      sort: { field: 'code', direction: 'asc' },
      filters: { name: 'Finance re' },
    });
  });

  it('drops fields that are not allowlisted and option values that are not offered', async () => {
    const { fixture, host, button } = await setup();
    host.query.set({
      page: 1,
      pageSize: 10,
      sort: null,
      filters: { status: "active' OR 1=1 --", owner: 'IT', name: '  ok ' },
    });
    await fixture.whenStable();
    button('Apply filters')!.click();
    await fixture.whenStable();
    expect(host.queries.at(-1)!.filters).toEqual({ name: 'ok' });
  });

  it('keeps an offered option value as a string code, leading zeros and all', async () => {
    const { fixture, host, button } = await setup();
    host.query.set({ page: 2, pageSize: 10, sort: null, filters: { status: 'retired' } });
    await fixture.whenStable();
    button('Apply filters')!.click();
    await fixture.whenStable();
    expect(host.queries.at(-1)).toEqual({
      page: 1,
      pageSize: 10,
      sort: null,
      filters: { status: 'retired' },
    });
  });

  it('clears every filter and leaves the filters key out', async () => {
    const { fixture, host, button, typeName } = await setup();
    await typeName('read');
    button('Clear filters')!.click();
    await fixture.whenStable();
    expect(host.queries.at(-1)).toEqual({
      page: 1,
      pageSize: 10,
      sort: { field: 'code', direction: 'asc' },
    });
    expect(
      (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('iam-textbox input')!
        .value,
    ).toBe('');
  });

  it('shows no filter bar or export button unless the caller asks for them', async () => {
    @Component({
      imports: [IamDataGrid],
      template: `<iam-data-grid
        caption="Plain"
        [columns]="[]"
        [rows]="[]"
        [totalCount]="0"
        [query]="q"
      />`,
    })
    class Plain {
      readonly q: IamGridQuery = { page: 1, pageSize: 10, sort: null };
    }
    const fixture = TestBed.createComponent(Plain);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('.iam-grid__filters')).toBeNull();
    expect(root.textContent).not.toContain('Export CSV');
  });
});

describe('iam-data-grid filter form lifecycle', () => {
  it('keeps typed but unapplied filter text when the user pages or sorts', async () => {
    const { fixture, root, host, typeName } = await setup();
    await typeName('half typed');
    host.query.set({ ...host.query(), page: 2 });
    await fixture.whenStable();
    host.query.set({ ...host.query(), sort: { field: 'code', direction: 'desc' } });
    await fixture.whenStable();
    expect(
      root.querySelector<HTMLInputElement>('.iam-grid__filters iam-textbox input')!.value,
    ).toBe('half typed');
  });

  it('keeps the form when the caller passes a new array with the same filters', async () => {
    const { fixture, root, host, typeName } = await setup();
    await typeName('kept');
    host.filters.set([...host.filters()]);
    await fixture.whenStable();
    expect(
      root.querySelector<HTMLInputElement>('.iam-grid__filters iam-textbox input')!.value,
    ).toBe('kept');
  });

  it('follows a reset of the applied filters by the caller', async () => {
    const { fixture, root, host } = await setup();
    host.query.set({ ...host.query(), filters: { name: 'applied' } });
    await fixture.whenStable();
    const input = root.querySelector<HTMLInputElement>('.iam-grid__filters iam-textbox input')!;
    expect(input.value).toBe('applied');
    host.query.set({ page: 1, pageSize: 10, sort: null });
    await fixture.whenStable();
    expect(input.value).toBe('');
  });

  it('names each clear button after its filter', async () => {
    const { fixture, root, host } = await setup();
    host.query.set({ ...host.query(), filters: { status: 'active' } });
    await fixture.whenStable();
    expect(root.querySelector('.iam-dropdown__clear')!.getAttribute('aria-label')).toBe(
      'Clear Status',
    );
  });
});

describe('iam-data-grid export', () => {
  afterEach(() => vi.restoreAllMocks());

  it('asks the server for the whole filtered result and downloads it under a safe name', async () => {
    const { fixture, host, button } = await setup();
    const created = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const clicks: HTMLAnchorElement[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push(this);
    });
    const calls: IamGridExportQuery[] = [];
    host.query.set({ page: 4, pageSize: 25, sort: null, filters: { name: 'x', bogus: 'y' } });
    host.exportAction.set(async (q) => {
      calls.push(q);
      return {
        fileName: '../../etc/passwd\u0000.csv',
        content: new Blob(['<script>'], { type: 'text/html' }),
      };
    });
    await fixture.whenStable();

    button('Export CSV')!.click();
    await fixture.whenStable();
    await new Promise((r) => setTimeout(r));
    // No paging in an export request.
    expect(calls).toEqual([{ sort: null, filters: { name: 'x' } }]);
    expect(created).toHaveBeenCalledOnce();
    // The server's content type is not trusted: the download is always CSV.
    expect((created.mock.calls[0][0] as Blob).type).toBe('text/csv;charset=utf-8');
    expect(clicks).toHaveLength(1);
    expect(clicks[0].download).toBe('etc_passwd_.csv');
    expect(clicks[0].isConnected).toBe(false);
  });

  it('keeps the object URL long enough for the download, then revokes it', async () => {
    const { fixture, host, button } = await setup();
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    const revoked = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    host.exportAction.set(async () => ({ fileName: 'x.csv', content: new Blob(['a']) }));
    await fixture.whenStable();
    button('Export CSV')!.click();
    await new Promise((r) => setTimeout(r, 20));
    expect(revoked).not.toHaveBeenCalled();
    await new Promise((r) => setTimeout(r, 1100));
    expect(revoked).toHaveBeenCalledWith('blob:fake');
  });

  it('does not send a sort on a column that is not sortable', async () => {
    const { fixture, host, button } = await setup();
    const calls: IamGridExportQuery[] = [];
    host.query.set({ page: 1, pageSize: 10, sort: { field: 'name', direction: 'asc' } });
    host.exportAction.set(async (q) => {
      calls.push(q);
      return { fileName: 'x.csv', content: new Blob(['a']) };
    });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    await fixture.whenStable();
    button('Export CSV')!.click();
    await new Promise((r) => setTimeout(r));
    expect(calls).toEqual([{ sort: null }]);
  });

  it('aborts the export and downloads nothing when the grid is destroyed', async () => {
    const { fixture, host, button } = await setup();
    const clicks = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);
    let finish!: () => void;
    let seen!: AbortSignal;
    host.exportAction.set(
      (_q, signal) =>
        new Promise((resolve) => {
          seen = signal;
          finish = () => resolve({ fileName: 'x.csv', content: new Blob(['a']) });
        }),
    );
    await fixture.whenStable();
    button('Export CSV')!.click();
    await fixture.whenStable();
    fixture.destroy();
    expect(seen.aborted).toBe(true);
    finish();
    await new Promise((r) => setTimeout(r));
    expect(clicks).not.toHaveBeenCalled();
  });

  it('shows a generic alert when the export fails, without the server message', async () => {
    const { fixture, root, host, button } = await setup();
    host.exportAction.set(() => Promise.reject(new Error('SQL timeout on server-db-01')));
    await fixture.whenStable();
    button('Export CSV')!.click();
    await new Promise((r) => setTimeout(r));
    await fixture.whenStable();
    const alert = root.querySelector('[role=alert]')!;
    expect(alert.textContent).toContain('The export could not be created.');
    expect(root.textContent).not.toContain('server-db-01');
    expect(button('Export CSV')!.disabled).toBe(false);
  });
});

describe('CSV rule for server exports', () => {
  it.each([
    '=SUM(A1)',
    '+1',
    '-1',
    '@cmd',
    '\tx',
    '\rx',
    '\n=1+1',
    ' =HYPERLINK("http://x")',
    '\u3000=1',
    '\uFF1D1+1',
    '\uFF0Bcmd',
    '\uFF0D1',
    '\uFF20SUM(A1)',
    "=cmd|' /C calc'!A0",
  ])('neutralizes %j', (value) => {
    expect(neutralizeCsvCell(value)).toBe(`'${value}`);
  });

  it('leaves ordinary text, codes with leading zeros and empty values alone', () => {
    expect(neutralizeCsvCell('Finance read')).toBe('Finance read');
    expect(neutralizeCsvCell('0100')).toBe('0100');
    expect(neutralizeCsvCell(null)).toBe('');
    expect(neutralizeCsvCell(undefined)).toBe('');
  });

  it('quotes fields per RFC 4180 after neutralizing', () => {
    expect(toCsvField('a,b')).toBe('"a,b"');
    expect(toCsvField('say "hi"')).toBe('"say ""hi"""');
    expect(toCsvField('line\nbreak')).toBe('"line\nbreak"');
    expect(toCsvField('=1+1,2')).toBe(`"'=1+1,2"`);
    expect(toCsvRow(['0100', '=x', 'a"b'])).toBe(`0100,'=x,"a""b"`);
  });

  it('makes download names safe', () => {
    expect(safeCsvFileName('entitlements 2026-11.csv')).toBe('entitlements_2026-11.csv');
    expect(safeCsvFileName('..\\..\\x.exe')).toBe('x.exe.csv');
    expect(safeCsvFileName('')).toBe('export.csv');
    expect(safeCsvFileName(null)).toBe('export.csv');
    expect(safeCsvFileName('a'.repeat(300))).toHaveLength(100);
    expect(safeCsvFileName('CON')).toBe('export_CON.csv');
    expect(safeCsvFileName('nul.csv')).toBe('export_nul.csv');
    expect(safeCsvFileName('com1.txt')).toBe('export_com1.txt.csv');
    expect(safeCsvFileName('console')).toBe('console.csv');
  });
});
