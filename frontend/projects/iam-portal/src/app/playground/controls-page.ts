import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import {
  IamAutocomplete,
  IamAutocompletePage,
  IamButton,
  IamCheckbox,
  IamConfirmService,
  IamDatePicker,
  IamDateRange,
  IamDateRangeValue,
  IamDropdown,
  IamFieldError,
  IamFormField,
  IamOption,
  IamProblem,
  IamRadioGroup,
  IamTextarea,
  IamTextbox,
  IamToastService,
  IamToggle,
  IamTooltip,
  IamValidationSummary,
  IsoDate,
  fieldErrorsFor,
  readProblemDetails,
  todayIso,
} from '@iam/ui';

const COMPANIES: readonly IamOption<string>[] = [
  { value: '0100', label: 'ABC Holdings (0100)' },
  { value: '0200', label: 'Bravo Ltd (0200)' },
  { value: '0300', label: 'Charlie GmbH (0300)', disabled: true },
  { value: '0400', label: 'Delta Inc (0400)' },
];

/** Form control name -> visible label; control names double as the controls' ids (controlId). */
const LABELS: Record<string, string> = {
  displayName: 'Display name',
  company: 'Company',
  level: 'Access level',
  justification: 'Business justification',
  accept: 'Confirmation',
  owner: 'Line manager',
  startDate: 'Start date',
  period: 'Access period',
};

/** 60 synthetic people for the fake directory search (no real data). */
const PEOPLE: readonly IamOption<string>[] = Array.from({ length: 60 }, (_, i) => {
  const first = ['Alex', 'Sam', 'Jordan', 'Taylor', 'Morgan', 'Casey'][i % 6];
  const last = [
    'Lee',
    'Patel',
    'Garcia',
    'Okafor',
    'Novak',
    'Kim',
    'Silva',
    'Haddad',
    'Berg',
    'Ito',
  ][i % 10];
  const id = `E${String(i + 1).padStart(5, '0')}`;
  return { value: id, label: `${first} ${last} (${id})` };
});

const LEVELS: readonly IamOption<string>[] = [
  { value: 'read', label: 'Read' },
  { value: 'write', label: 'Write' },
];

/** Every S1-09 control in one typed reactive form, with simulated server-side validation. Synthetic data only. */
@Component({
  selector: 'iam-controls-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    IamButton,
    IamAutocomplete,
    IamCheckbox,
    IamDatePicker,
    IamDateRange,
    IamDropdown,
    IamFormField,
    IamRadioGroup,
    IamTextarea,
    IamTextbox,
    IamToggle,
    IamTooltip,
    IamValidationSummary,
  ],
  template: `
    <h1 class="mt-0 text-2xl font-semibold">Controls</h1>
    <p class="text-iam-muted">
      A playground for the shared controls. Submit with the display name <strong>taken</strong> to
      see server-side validation errors.
    </p>

    <iam-validation-summary class="mb-4 block" [errors]="summary()" [general]="general()" />

    <form class="flex max-w-xl flex-col gap-5" [formGroup]="form" (ngSubmit)="submit()" novalidate>
      <iam-form-field
        label="Display name"
        hint="Shown to approvers"
        controlId="displayName"
        [serverErrors]="serverErrors('displayName')"
      >
        <iam-textbox formControlName="displayName" [maxLength]="200" autocomplete="off" />
      </iam-form-field>

      <iam-form-field label="Company" controlId="company" [serverErrors]="serverErrors('company')">
        <iam-dropdown formControlName="company" [options]="companies" [searchable]="true" />
      </iam-form-field>

      <iam-radio-group
        formControlName="level"
        legend="Access level"
        controlId="level"
        [options]="levels"
        [serverErrors]="serverErrors('level')"
      />

      <iam-form-field
        label="Business justification"
        hint="Why the access is needed"
        controlId="justification"
        [serverErrors]="serverErrors('justification')"
      >
        <iam-textarea formControlName="justification" [maxLength]="500" />
      </iam-form-field>

      <iam-form-field
        label="Line manager"
        hint="Type at least two letters, e.g. pat. Type fail to see a search error."
        controlId="owner"
        [serverErrors]="serverErrors('owner')"
      >
        <iam-autocomplete formControlName="owner" [search]="searchPeople" />
      </iam-form-field>

      <iam-form-field
        label="Start date"
        hint="Today or later"
        controlId="startDate"
        [serverErrors]="serverErrors('startDate')"
      >
        <iam-date-picker formControlName="startDate" [min]="today" />
      </iam-form-field>

      <div class="flex flex-col gap-2">
        <iam-date-range
          formControlName="period"
          legend="Access period"
          startLabel="Access from"
          endLabel="Access until"
          hint="The end date must not be before the start date."
          controlId="period"
          [min]="today"
          [serverErrors]="serverErrors('period')"
        />
        <button
          iamButton
          variant="ghost"
          class="self-start"
          iamTooltip="Time-bound access is removed automatically at the end of the period."
        >
          About access periods
        </button>
      </div>

      <iam-toggle formControlName="notify" label="Email me about this request" />
      <iam-checkbox
        formControlName="accept"
        controlId="accept"
        label="I confirm the information is correct"
      />

      <div class="flex gap-2">
        <button iamButton variant="primary" type="submit" [loading]="saving()">Submit</button>
        <button iamButton variant="danger" (click)="reset()">Reset form</button>
      </div>
    </form>
  `,
})
export class ControlsPage {
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly toasts = inject(IamToastService);
  private readonly confirmations = inject(IamConfirmService);
  private readonly summaryRef = viewChild.required(IamValidationSummary);

  protected readonly companies = COMPANIES;
  protected readonly levels = LEVELS;
  protected readonly saving = signal(false);
  protected readonly summary = signal<readonly IamFieldError[]>([]);
  protected readonly general = signal<readonly string[]>([]);
  private readonly problem = signal<IamProblem | null>(null);

  protected readonly form = this.fb.group({
    displayName: this.fb.control('', [Validators.required, Validators.maxLength(200)]),
    company: this.fb.control<string | null>(null, Validators.required),
    level: this.fb.control<string | null>(null, Validators.required),
    justification: this.fb.control('', Validators.maxLength(500)),
    notify: this.fb.control(false),
    accept: this.fb.control(false, Validators.requiredTrue),
    owner: this.fb.control<string | null>(null),
    startDate: this.fb.control<IsoDate | null>(null),
    period: this.fb.control<IamDateRangeValue | null>(null),
  });

  protected readonly today = todayIso();

  /** Stands in for a directory search API: bounded results, honours cancellation, "fail" simulates an error. */
  protected readonly searchPeople = async (
    query: string,
    signal: AbortSignal,
  ): Promise<IamAutocompletePage<string>> => {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(resolve, 200);
      signal.addEventListener('abort', () => {
        clearTimeout(timer);
        reject(signal.reason);
      });
    });
    if (query.toLowerCase() === 'fail') {
      throw new Error('Directory unavailable');
    }

    const matches = PEOPLE.filter((p) => p.label.toLowerCase().includes(query.toLowerCase()));
    return { items: matches.slice(0, 10), hasMore: matches.length > 10 };
  };

  protected serverErrors(field: string): readonly string[] {
    const problem = this.problem();
    return problem ? fieldErrorsFor(problem, field) : [];
  }

  protected async submit(): Promise<void> {
    this.problem.set(null);
    this.general.set([]);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.summary.set(this.clientErrors());
      this.summaryRef().focus();
      return;
    }

    this.saving.set(true);
    const body = await fakeSubmit(this.form.getRawValue().displayName);
    this.saving.set(false);
    if (body) {
      const problem = readProblemDetails(body);
      this.problem.set(problem);
      this.general.set([problem.title]);
      // Map the API's field names onto this form's own labels and ids; unknown fields keep a generic label and
      // are not linked (feature screens must do the same: never show raw API names or link to unknown ids).
      this.summary.set(
        Object.entries(problem.fieldErrors).flatMap(([field, messages]) => {
          const known = Object.keys(LABELS).find(
            (name) => name.toLowerCase() === field.toLowerCase(),
          );
          return messages.map((message) => ({
            label: known ? LABELS[known] : 'Request',
            message,
            fieldId: known,
          }));
        }),
      );
      this.summaryRef().focus();
      return;
    }

    this.summary.set([]);
    this.toasts.show('Request submitted.', 'success');
  }

  protected async reset(): Promise<void> {
    const confirmed = await this.confirmations.confirm({
      heading: 'Reset the form?',
      message: 'Everything you entered will be cleared.',
      confirmLabel: 'Reset',
      destructive: true,
    });
    if (confirmed) {
      this.form.reset();
      this.problem.set(null);
      this.summary.set([]);
      this.general.set([]);
      this.toasts.show('Form reset.', 'info');
    }
  }

  private clientErrors(): IamFieldError[] {
    return Object.entries(this.form.controls)
      .filter(([, control]) => control.invalid)
      .map(([name, control]) => ({
        label: LABELS[name] ?? name,
        fieldId: name,
        message:
          control.errors?.['required'] || control.errors?.['requiredTrue']
            ? 'is required'
            : 'is invalid',
      }));
  }
}

/** Stands in for the API: returns a ValidationProblemDetails body when the name is "taken". */
async function fakeSubmit(displayName: string): Promise<unknown | null> {
  await new Promise((resolve) => setTimeout(resolve, 300));
  return displayName.toLowerCase() === 'taken'
    ? {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { DisplayName: ['That display name is already in use.'] },
      }
    : null;
}
