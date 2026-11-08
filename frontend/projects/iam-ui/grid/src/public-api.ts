/*
 * @iam/ui/grid (S1-09, ADR-0019): the AG Grid wrapper, in its own entry point so AG Grid is bundled only into pages
 * that show a grid. This is the only code allowed to import AG Grid (enforced by ESLint).
 */
export {
  IAM_GRID_PAGE_SIZES,
  IamDataGrid,
  type IamGridColumn,
  type IamGridQuery,
  type IamSortDirection,
} from './data-grid';
