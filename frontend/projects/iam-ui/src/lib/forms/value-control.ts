import {
  DestroyRef,
  Directive,
  DoCheck,
  Signal,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import {
  AbstractControl,
  ControlValueAccessor,
  NgControl,
  ValidationErrors,
  ValidatorFn,
} from '@angular/forms';
import { IAM_FIELD_CONTEXT, IamFieldControl, iamId, safeId } from './field';

/**
 * Shared ControlValueAccessor plumbing for IAM form controls: value, disabled and touched state as Signals, the
 * NgControl binding without an NG_VALUE_ACCESSOR provider cycle, and the label/hint/error ids from the enclosing
 * `iam-form-field`. Subclasses render semantic HTML and call `commit()` / `markTouched()`.
 */
@Directive()
export abstract class IamValueControl<T> implements ControlValueAccessor, IamFieldControl, DoCheck {
  protected readonly ngControl = inject(NgControl, { self: true, optional: true });
  protected readonly field = inject(IAM_FIELD_CONTEXT, { optional: true });
  private readonly destroyRef = inject(DestroyRef);

  /** Current value as the control shows it. */
  readonly value = signal<T>(this.emptyValue());
  readonly disabledByForm = signal(false);
  private readonly localTouched = signal(false);
  /**
   * Bumped on every value, status or touched change of the bound control. Computeds that read the form control's
   * (non-signal) state, such as its errors, must read this: `touched()` alone stays true across changes and would let
   * a stale error list survive.
   */
  protected readonly revision = signal(0);
  private watched: AbstractControl | null = null;
  private unwatch: (() => void) | null = null;
  private onChange: (value: T) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  /**
   * Id for the control when used without `iam-form-field` (validated; inside a field, set `controlId` on the field).
   * Not named `id`, which would also put the id on the host element and duplicate it.
   */
  readonly controlId = input<string | null>(null);
  private readonly ownId = iamId('iam-input');

  /**
   * Disabled state for standalone use. Inside a form the form control's own disabled state is the single source of
   * truth and this input is ignored (bind `[disabled]` together with `formControl` and Angular warns).
   */
  readonly disabled = input<boolean>(false);

  /** Whether the control is disabled: the form's state when bound to a form, otherwise the `disabled` input. */
  readonly isDisabled = computed(() => (this.ngControl ? this.disabledByForm() : this.disabled()));

  /** Subclasses that validate their own input (e.g. a date picker) set this and override `validate`. */
  protected readonly validates: boolean = false;
  private readonly boundValidator: ValidatorFn = (control) => this.validate(control);

  /** The element id the field's label points at. */
  readonly inputId = computed(
    () => this.field?.inputId() ?? safeId(this.controlId()) ?? this.ownId,
  );

  readonly touched: Signal<boolean> = computed(() => {
    this.revision();
    return this.control?.touched ?? this.localTouched();
  });

  /** aria-invalid: true when the field shows at least one error. */
  readonly ariaInvalid = computed(() => (this.field?.invalid() ? 'true' : null));

  /** aria-describedby: hint and error ids from the field. */
  readonly ariaDescribedBy = computed(() => this.field?.describedBy() ?? null);

  /** aria-required mirrors the field's required flag (the validator itself lives on the form control). */
  readonly ariaRequired = computed(() => (this.field?.required() ? 'true' : null));

  constructor() {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
    }

    this.destroyRef.onDestroy(() => this.unwatch?.());

    this.field?.registered.set(this);
  }

  get control(): AbstractControl | null {
    return this.ngControl?.control ?? null;
  }

  /**
   * Watches the bound control's events once the form directive has created it, and again whenever the directive
   * swaps in a different control instance (for example a rebound [formControl]).
   */
  ngDoCheck(): void {
    const control = this.control;
    if (control === this.watched) {
      // Feature code may replace the validators (setValidators / clearValidators, e.g. "required only when X"):
      // the control's own rules must survive that, or text that is not a date would pass as an empty value.
      if (this.validates && control && !control.hasValidator(this.boundValidator)) {
        control.addValidators(this.boundValidator);
        control.updateValueAndValidity({ emitEvent: false });
        this.bump();
      }

      return;
    }

    this.unwatch?.();
    if (this.validates) {
      this.watched?.removeValidators(this.boundValidator);
    }

    this.watched = control;
    this.unwatch = null;
    if (control) {
      const subscription = control.events.subscribe(() => this.bump());
      this.unwatch = () => subscription.unsubscribe();
      if (this.validates) {
        control.addValidators(this.boundValidator);
        control.updateValueAndValidity({ emitEvent: false });
      }
    }

    this.bump();
  }

  writeValue(value: T | null | undefined): void {
    this.value.set(this.normalize(value));
    this.bump();
  }

  registerOnChange(fn: (value: T) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabledByForm.set(isDisabled);
  }

  /** Own validation rules; only used when `validates` is true. Messages are plain text, shown by the form field. */
  protected validate(control: AbstractControl): ValidationErrors | null {
    void control;
    return null;
  }

  /**
   * Re-runs validation after a change the form cannot see (for example unparsable typed text). No value or status
   * events are emitted, so subscribers do not see a second valueChanges for one edit; the form's validity and this
   * control's error display are still updated.
   */
  protected revalidate(): void {
    this.watched?.updateValueAndValidity({ emitEvent: false });
    this.bump();
  }

  /** Called by subclasses when the user changes the value. */
  protected commit(value: T): void {
    this.value.set(value);
    this.onChange(value);
    this.bump();
  }

  /** Called by subclasses when the user leaves the control. */
  protected markTouched(): void {
    this.localTouched.set(true);
    this.onTouched();
    this.bump();
  }

  /** The value shown when the form writes null or undefined. */
  protected abstract emptyValue(): T;

  /** Converts a value written by the form into the control's type. */
  protected abstract normalize(value: T | null | undefined): T;

  private bump(): void {
    this.revision.update((r) => r + 1);
    this.field?.revision.update((r) => r + 1);
  }
}
