import { plainText } from './plain-text';

/**
 * Strict reader for RFC 9457 ProblemDetails returned by the IAM API (BACKEND_INPUT_VALIDATION: "Display safe
 * ProblemDetails validation errors"). Everything is bounded and treated as plain text; unknown fields are ignored;
 * anything malformed yields a generic message rather than raw server output.
 */
export interface IamProblem {
  readonly title: string;
  readonly detail: string | null;
  readonly status: number | null;
  /** Field name → messages, from `errors` (ASP.NET Core ValidationProblemDetails). */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
}

const MAX_TITLE = 200;
const MAX_DETAIL = 1000;
const MAX_FIELDS = 50;
const MAX_MESSAGES_PER_FIELD = 10;
const MAX_MESSAGE = 500;
const FIELD_NAME = /^[A-Za-z][A-Za-z0-9_.[\]$-]{0,99}$/;
const GENERIC_TITLE = 'The request could not be completed.';

export function readProblemDetails(body: unknown): IamProblem {
  if (body === null || typeof body !== 'object' || Array.isArray(body)) {
    return { title: GENERIC_TITLE, detail: null, status: null, fieldErrors: {} };
  }

  const record = body as Record<string, unknown>;
  // No prototype: a field named like an Object member can never collide with one.
  const fieldErrors: Record<string, string[]> = Object.create(null) as Record<string, string[]>;
  const errors = record['errors'];
  if (errors !== null && typeof errors === 'object' && !Array.isArray(errors)) {
    for (const [name, messages] of Object.entries(errors as Record<string, unknown>).slice(
      0,
      MAX_FIELDS,
    )) {
      if (!FIELD_NAME.test(name) || !Array.isArray(messages)) {
        continue;
      }

      const clean = messages
        .filter((m): m is string => typeof m === 'string')
        .slice(0, MAX_MESSAGES_PER_FIELD)
        .map((m) => bounded(m, MAX_MESSAGE))
        .filter((m) => m.length > 0);
      if (clean.length > 0) {
        fieldErrors[name] = clean;
      }
    }
  }

  const status = record['status'];
  return {
    title:
      typeof record['title'] === 'string' && record['title'].trim()
        ? bounded(record['title'], MAX_TITLE)
        : GENERIC_TITLE,
    detail:
      typeof record['detail'] === 'string' && record['detail'].trim()
        ? bounded(record['detail'], MAX_DETAIL)
        : null,
    status:
      typeof status === 'number' && Number.isInteger(status) && status >= 100 && status <= 599
        ? status
        : null,
    fieldErrors,
  };
}

/** Field errors for one form control name, matched case-insensitively ("displayName" ↔ "DisplayName"). */
export function fieldErrorsFor(problem: IamProblem, controlName: string): readonly string[] {
  const key = Object.keys(problem.fieldErrors).find(
    (k) => k.toLowerCase() === controlName.toLowerCase(),
  );
  return key ? problem.fieldErrors[key] : [];
}

function bounded(text: string, max: number): string {
  return plainText(text, max);
}
