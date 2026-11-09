/*
 * Public API of @iam/ui (S1-09, ADR-0019). Feature code imports only from '@iam/ui'; deep imports into the library
 * and direct AG Grid imports are blocked by ESLint.
 * The AG Grid wrapper lives in the secondary entry point @iam/ui/grid.
 */
export { IamButton, IamIconButton, type IamButtonVariant } from './lib/controls/button';
export { IamCheckbox, IamRadioGroup, IamToggle, type IamOption } from './lib/controls/choice';
export { IAM_DROPDOWN_MAX_QUERY, IamDropdown } from './lib/controls/dropdown';
export { IamTextarea } from './lib/controls/textarea';
export { IamTextbox, type IamTextboxType, type IamTrimPolicy } from './lib/controls/textbox';
export {
  IamToastOutlet,
  IamToastService,
  IamValidationSummary,
  type IamFieldError,
  type IamToast,
  type IamToastKind,
} from './lib/feedback/feedback';
export {
  fieldErrorsFor,
  readProblemDetails,
  type IamProblem,
} from './lib/feedback/problem-details';
export { iamId } from './lib/forms/field';
export { IamFormField } from './lib/forms/form-field';
export {
  IamConfirmService,
  IamDialog,
  IamDialogService,
  type IamConfirmOptions,
  type IamDialogOptions,
} from './lib/overlays/dialog';
export {
  IAM_AUTOCOMPLETE_MAX_QUERY,
  IAM_AUTOCOMPLETE_MAX_RESULTS,
  IAM_AUTOCOMPLETE_MIN_CHARS,
  IamAutocomplete,
  type IamAutocompletePage,
  type IamAutocompleteSearch,
} from './lib/controls/autocomplete';
export { IamDatePicker } from './lib/dates/date-picker';
export { IamDateRange, type IamDateRangeValue } from './lib/dates/date-range';
export { isIsoDate, todayIso, type IsoDate } from './lib/dates/date-only';
export { IamTooltip } from './lib/overlays/tooltip';
