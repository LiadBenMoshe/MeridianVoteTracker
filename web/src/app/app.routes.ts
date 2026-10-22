import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'imports' },
  { path: 'imports', loadComponent: () => import('./pages/imports.page').then((m) => m.ImportsPage) },
  { path: 'imports/:id', loadComponent: () => import('./pages/import-report.page').then((m) => m.ImportReportPage) },
  { path: 'review', loadComponent: () => import('./pages/review.page').then((m) => m.ReviewPage) },
  { path: 'votes', loadComponent: () => import('./pages/votes.page').then((m) => m.VotesPage) },
  { path: 'votes/detail', loadComponent: () => import('./pages/vote-detail.page').then((m) => m.VoteDetailPage) },
  { path: 'snapshots', loadComponent: () => import('./pages/snapshots.page').then((m) => m.SnapshotsPage) },
  { path: 'snapshots/:id', loadComponent: () => import('./pages/snapshot-detail.page').then((m) => m.SnapshotDetailPage) },
  { path: '**', redirectTo: 'imports' },
];
