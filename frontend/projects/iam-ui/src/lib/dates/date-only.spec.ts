import {
  addDays,
  addMonths,
  clampDate,
  dayOfWeek,
  firstDayOfWeek,
  formatIsoDate,
  isIsoDate,
  monthGrid,
  numericOrder,
  parseUserDate,
  patternHint,
  todayIso,
  weekdayNames,
} from './date-only';

describe('date-only values', () => {
  it.each([
    ['2026-11-08', true],
    ['2028-02-29', true],
    ['2026-02-29', false],
    ['2026-02-30', false],
    ['2026-13-01', false],
    ['2026-00-10', false],
    ['0999-01-01', false],
    ['2026-1-8', false],
    ['2026-11-08\n', false],
    ['2026-11-08T00:00:00Z', false],
    [20261108, false],
    [null, false],
  ])('isIsoDate(%p) is %p', (value, expected) => expect(isIsoDate(value)).toBe(expected));

  it('adds days and months across month, year and leap-year boundaries', () => {
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(addMonths('2026-01-31', 1)).toBe('2026-02-28');
    expect(addMonths('2028-01-31', 1)).toBe('2028-02-29');
    expect(addMonths('2026-01-15', -2)).toBe('2025-11-15');
    expect(addMonths('2026-11-08', 12)).toBe('2027-11-08');
  });

  it('never shifts a date across daylight-saving changes (UTC arithmetic)', () => {
    // DST changes in Europe (late March / late October) and the US (March / November).
    expect(addDays('2026-03-28', 1)).toBe('2026-03-29');
    expect(addDays('2026-03-29', 1)).toBe('2026-03-30');
    expect(addDays('2026-10-24', 1)).toBe('2026-10-25');
    expect(addDays('2026-10-25', 1)).toBe('2026-10-26');
    expect(addDays('2026-11-01', 1)).toBe('2026-11-02');
    expect(formatIsoDate('2026-10-25', 'en-GB')).toBe('25 Oct 2026');
  });

  it('knows the day of the week and clamps to a range', () => {
    expect(dayOfWeek('2026-11-08')).toBe(0);
    expect(clampDate('2026-01-01', '2026-02-01', '2026-03-01')).toBe('2026-02-01');
    expect(clampDate('2026-05-01', '2026-02-01', '2026-03-01')).toBe('2026-03-01');
    expect(clampDate('2026-02-15', null, null)).toBe('2026-02-15');
  });

  it('uses the local calendar for today', () => {
    expect(todayIso(new Date(2026, 10, 8, 23, 59))).toBe('2026-11-08');
    expect(todayIso(new Date(2026, 0, 1, 0, 0))).toBe('2026-01-01');
  });

  it('follows the locale field order and pattern', () => {
    expect(numericOrder('en-GB')).toEqual(['day', 'month', 'year']);
    expect(numericOrder('en-US')).toEqual(['month', 'day', 'year']);
    expect(numericOrder('de-DE')).toEqual(['day', 'month', 'year']);
    expect(patternHint('en-GB')).toBe('dd/mm/yyyy');
    expect(patternHint('en-US')).toBe('mm/dd/yyyy');
    expect(patternHint('de-DE')).toBe('dd.mm.yyyy');
  });

  it.each([
    ['31/12/2026', 'en-GB', '2026-12-31'],
    ['12/31/2026', 'en-US', '2026-12-31'],
    ['31.12.2026', 'de-DE', '2026-12-31'],
    ['1/2/2026', 'en-GB', '2026-02-01'],
    ['1/2/2026', 'en-US', '2026-01-02'],
    ['2026-12-31', 'en-US', '2026-12-31'],
    [' 29/02/2028 ', 'en-GB', '2028-02-29'],
    ['29/02/2026', 'en-GB', null],
    ['31/12/26', 'en-GB', null],
    ['12/31/2026', 'en-GB', null],
    ['31/12', 'en-GB', null],
    ['tomorrow', 'en-GB', null],
    ['', 'en-GB', null],
    ['31/12/2026/1', 'en-GB', null],
  ])('parses %p in %s as %p', (text, locale, expected) =>
    expect(parseUserDate(text, locale)).toBe(expected),
  );

  it('knows where the week starts', () => {
    expect([0, 1]).toContain(firstDayOfWeek('en-US'));
    expect(firstDayOfWeek('en-GB')).toBe(1);
    expect(firstDayOfWeek('not a locale')).toBe(1);
    const names = weekdayNames('en-GB', 1);
    expect(names[0].long).toBe('Monday');
    expect(names[6].long).toBe('Sunday');
  });

  it('builds a 6x7 month grid starting on the first day of the week', () => {
    const grid = monthGrid('2026-11-15', 1);
    expect(grid).toHaveLength(6);
    expect(grid.every((w) => w.length === 7)).toBe(true);
    expect(grid[0][0]).toBe('2026-10-26');
    expect(grid[0][6]).toBe('2026-11-01');
    expect(grid.flat()).toContain('2026-11-30');
  });
});
