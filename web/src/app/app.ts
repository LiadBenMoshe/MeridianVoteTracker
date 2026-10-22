import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Api } from './api/api';
import { apiReviewSummaryGet } from './api/functions';
import { ReviewSummaryDto } from './api/models';
import { Session } from './core/session';
import { Poller, ToastsComponent } from './core/ui';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastsComponent],
  template: `
    <header class="topbar">
      <div><strong>Meridian Vote Tracker</strong> <span class="muted-light">Northwind Policy Institute</span></div>
      <label class="switcher">
        Switch user
        <select (change)="switchUser($any($event.target).value)">
          @for (u of session.users(); track u.id) {
            <option [value]="u.id" [selected]="u.id === session.userId()">{{ u.name }} · {{ u.role }}</option>
          }
        </select>
      </label>
    </header>
    <div class="layout">
      <nav class="sidenav">
        <a routerLink="/imports" routerLinkActive="active">Imports</a>
        <a routerLink="/review" routerLinkActive="active"
          >Review
          @if (pending() > 0) {
            <span class="badge">{{ pending() }}</span>
          }
        </a>
        <a routerLink="/votes" routerLinkActive="active">Votes</a>
        <a routerLink="/snapshots" routerLinkActive="active">Snapshots</a>
        <div class="nav-foot muted small">Signed in as {{ session.user()?.name }} ({{ session.user()?.role }})</div>
      </nav>
      <main class="content">
        @if (ready()) {
          <router-outlet />
        }
      </main>
    </div>
    <app-toasts />
  `,
})
export class App {
  readonly session = inject(Session);
  private readonly api = inject(Api);
  readonly pending = signal(0);
  readonly ready = signal(false);
  readonly summary = signal<ReviewSummaryDto | null>(null);
  private readonly poll = new Poller();

  constructor() {
    this.session.load().finally(() => this.ready.set(true));
    this.poll.start(() => this.refreshBadge(), 10000);
  }

  async refreshBadge() {
    try {
      const s = await this.api.invoke(apiReviewSummaryGet);
      this.summary.set(s);
      this.pending.set(s.pendingConflicts + s.extractionsToReview);
    } catch {
      /* badge is best effort */
    }
  }

  switchUser(id: string) {
    this.session.switchTo(id);
    // Re-render the current page with the new identity (permissions come from the API).
    this.ready.set(false);
    setTimeout(() => this.ready.set(true));
    this.refreshBadge();
  }
}
