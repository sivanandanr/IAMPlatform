import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Controls · IAM UI foundation',
    loadComponent: () => import('./playground/controls-page').then((m) => m.ControlsPage),
  },
  {
    path: 'grid',
    title: 'Data grid · IAM UI foundation',
    loadComponent: () => import('./playground/grid-page').then((m) => m.GridPage),
  },
  { path: '**', redirectTo: '' },
];
