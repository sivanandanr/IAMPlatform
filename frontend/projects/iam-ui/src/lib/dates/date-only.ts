/**
 * Date-only values (S1-10, ADR-0019): a calendar date is exchanged as an ISO `yyyy-MM-dd` string, never as a
 * JavaScript Date, so no time zone or daylight-saving change can ever shift it. All arithmetic uses UTC internally;
 * display and parsing use the browser's Intl for the user's locale. No date library.
 */
export type IsoDate = string;

// In JavaScript `$` without the m flag is end of input (unlike .NET, no trailing-newline match).
const ISO = /^(\d{4})-(\d{2})-(\d{2})$/;

export interface DateParts {
  readonly year: number;
  /** 1-12 */
  readonly month: number;
  /** 1-31 */
  readonly day: number;
}

export function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

/** True for a real calendar date in the form yyyy-MM-dd (years 1000-9999). 2026-02-30 is false; 2028-02-29 is true. */
export function isIsoDate(value: unknown): boolean {
  if (typeof value !== 'string') {
    return false;
  }

  const match = ISO.exec(value);
  if (!match) {
    return false;
  }

  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  return year >= 1000 && month >= 1 && month <= 12 && day >= 1 && day <= daysInMonth(year, month);
}

export function toParts(iso: IsoDate): DateParts {
  return {
    year: Number(iso.slice(0, 4)),
    month: Number(iso.slice(5, 7)),
    day: Number(iso.slice(8, 10)),
  };
}

export function fromParts({ year, month, day }: DateParts): IsoDate {
  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

function toUtc(iso: IsoDate): Date {
  const { year, month, day } = toParts(iso);
  return new Date(Date.UTC(year, month - 1, day));
}

function fromUtc(date: Date): IsoDate {
  return fromParts({
    year: date.getUTCFullYear(),
    month: date.getUTCMonth() + 1,
    day: date.getUTCDate(),
  });
}

export function addDays(iso: IsoDate, days: number): IsoDate {
  const date = toUtc(iso);
  date.setUTCDate(date.getUTCDate() + days);
  return fromUtc(date);
}

/** Moves by whole months, keeping the day where possible (31 Jan + 1 month = 28/29 Feb). */
export function addMonths(iso: IsoDate, months: number): IsoDate {
  const { year, month, day } = toParts(iso);
  const index = year * 12 + (month - 1) + months;
  const targetYear = Math.floor(index / 12);
  const targetMonth = (index % 12) + 1;
  return fromParts({
    year: targetYear,
    month: targetMonth,
    day: Math.min(day, daysInMonth(targetYear, targetMonth)),
  });
}

export function startOfMonth(iso: IsoDate): IsoDate {
  return `${iso.slice(0, 8)}01`;
}

/** 0 = Sunday … 6 = Saturday. */
export function dayOfWeek(iso: IsoDate): number {
  return toUtc(iso).getUTCDay();
}

/** ISO strings sort like dates. */
export function compareDates(a: IsoDate, b: IsoDate): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function clampDate(iso: IsoDate, min: IsoDate | null, max: IsoDate | null): IsoDate {
  if (min && iso < min) {
    return min;
  }

  return max && iso > max ? max : iso;
}

/** Today in the user's own calendar (local date parts, not UTC). */
export function todayIso(now: Date = new Date()): IsoDate {
  return fromParts({ year: now.getFullYear(), month: now.getMonth() + 1, day: now.getDate() });
}

/** Formats a date-only value for display; formatting in UTC means the day shown is always the day stored. */
export function formatIsoDate(
  iso: IsoDate,
  locale: string,
  style: 'medium' | 'long' | 'short' = 'medium',
): string {
  const { year, month, day } = toParts(iso);
  return new Intl.DateTimeFormat(locale, { dateStyle: style, timeZone: 'UTC' }).format(
    new Date(Date.UTC(year, month - 1, day)),
  );
}

/** The locale's numeric field order, e.g. ['day','month','year'] for en-GB, ['month','day','year'] for en-US. */
export function numericOrder(locale: string): readonly ('day' | 'month' | 'year')[] {
  const parts = new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    timeZone: 'UTC',
  }).formatToParts(new Date(Date.UTC(2026, 10, 23)));
  return parts
    .filter((p) => p.type === 'day' || p.type === 'month' || p.type === 'year')
    .map((p) => p.type as 'day' | 'month' | 'year');
}

/** A short pattern hint for the locale, e.g. "dd/mm/yyyy" or "mm/dd/yyyy". */
export function patternHint(locale: string): string {
  const names = { day: 'dd', month: 'mm', year: 'yyyy' } as const;
  const sample = new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    timeZone: 'UTC',
  }).formatToParts(new Date(Date.UTC(2026, 10, 23)));
  return sample
    .map((p) =>
      p.type === 'day' || p.type === 'month' || p.type === 'year' ? names[p.type] : p.value,
    )
    .join('');
}

/**
 * Parses what a user typed: always the ISO form yyyy-MM-dd, otherwise three numbers in the locale's order separated by
 * '/', '.', '-' or spaces. The year must have four digits (two-digit years are ambiguous). Returns null if the text is
 * not a real calendar date.
 */
export function parseUserDate(text: string, locale: string): IsoDate | null {
  // Bidi marks appear in some locales' own separators (ar: 04/03/2026 with U+200F before each '/'); they are not part of the date.
  const trimmed = text.replace(/[\u200E\u200F\u061C\u202A-\u202E\u2066-\u2069]/g, '').trim();
  if (isIsoDate(trimmed)) {
    return trimmed;
  }

  // Some locales end with a separator (e.g. "2026. 11. 08."), so empty pieces are ignored.
  const pieces = trimmed.split(/[\s./-]+/).filter((p) => p.length > 0);
  if (pieces.length !== 3 || pieces.some((p) => !/^\d{1,4}$/.test(p))) {
    return null;
  }

  const order = numericOrder(locale);
  const values: Partial<Record<'day' | 'month' | 'year', string>> = {};
  order.forEach((field, i) => (values[field] = pieces[i]));
  if (
    !values.year ||
    values.year.length !== 4 ||
    !values.month ||
    values.month.length > 2 ||
    !values.day ||
    values.day.length > 2
  ) {
    return null;
  }

  const iso = fromParts({
    year: Number(values.year),
    month: Number(values.month),
    day: Number(values.day),
  });
  return isIsoDate(iso) ? iso : null;
}

/** First day of the week for the locale (0 = Sunday, 1 = Monday), from Intl week data when the browser has it. */
export function firstDayOfWeek(locale: string): number {
  try {
    const intlLocale = new Intl.Locale(locale) as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number };
      weekInfo?: { firstDay: number };
    };
    const info = intlLocale.getWeekInfo?.() ?? intlLocale.weekInfo;
    if (info && info.firstDay >= 1 && info.firstDay <= 7) {
      return info.firstDay % 7;
    }
  } catch {
    // Unknown locale: fall through to the default.
  }

  return 1;
}

/** Weekday names starting at `firstDay`, short and long. */
export function weekdayNames(locale: string, firstDay: number): { short: string; long: string }[] {
  const short = new Intl.DateTimeFormat(locale, { weekday: 'short', timeZone: 'UTC' });
  const long = new Intl.DateTimeFormat(locale, { weekday: 'long', timeZone: 'UTC' });
  // 2026-11-01 is a Sunday.
  return Array.from({ length: 7 }, (_, i) => {
    const date = new Date(Date.UTC(2026, 10, 1 + ((firstDay + i) % 7)));
    return { short: short.format(date), long: long.format(date) };
  });
}

export function monthLabel(iso: IsoDate, locale: string): string {
  const { year, month } = toParts(iso);
  return new Intl.DateTimeFormat(locale, {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(new Date(Date.UTC(year, month - 1, 1)));
}

/** The 6×7 grid of dates shown for the month containing `iso`, starting on `firstDay`. */
export function monthGrid(iso: IsoDate, firstDay: number): IsoDate[][] {
  const first = startOfMonth(iso);
  const offset = (dayOfWeek(first) - firstDay + 7) % 7;
  const start = addDays(first, -offset);
  return Array.from({ length: 6 }, (_, week) =>
    Array.from({ length: 7 }, (_, d) => addDays(start, week * 7 + d)),
  );
}
