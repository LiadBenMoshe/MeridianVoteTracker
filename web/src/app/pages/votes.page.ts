import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiVotesGet, apiVotesPositionsBulkPost, apiVotesSessionsGet } from '../api/functions';
import { VoteListDto } from '../api/models';
import { Problem, fieldError, toProblem } from '../core/http';
import { Session } from '../core/session';
import { ProblemComponent, Toasts } from '../core/ui';

/** Screen 6: all votes with position, weight and open conflicts; votes without position are flagged (POS-04). */
@Component({
  selector: 'app-votes',
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <h1>Votes</h1>
    <p class="muted">All votes in the system, with Northwind's position and weight.</p>
    <div class="card filters">
      <label>Session
        <select [(ngModel)]="session" (change)="load(1)">
          <option [ngValue]="null">All</option>
          @for (s of sessions(); track s) { <option [ngValue]="s">{{ s }}</option> }
        </select>
      </label>
      <label>From <input type="date" [(ngModel)]="from" (change)="load(1)" /></label>
      <label>To <input type="date" [(ngModel)]="to" (change)="load(1)" /></label>
      <input placeholder="Search title or vote ID…" [(ngModel)]="search" (keyup.enter)="load(1)" />
      <label class="small"><input type="checkbox" [(ngModel)]="withoutPosition" (change)="load(1)" /> Without position only</label>
      <button (click)="load(1)">Search</button>
    </div>
    <app-problem [problem]="problem()" />

    @if (list(); as l) {
      @if (l.withoutPositionTotal > 0) {
        <div class="banner error">
          <strong>{{ l.withoutPositionTotal.toLocaleString() }} vote(s) have no position.</strong>
          Votes without a position are never silently included in a snapshot.
        </div>
      }
      @if (session_.isAdmin() && l.withoutPositionTotal > 0) {
        <div class="card row small">
          <strong>Admin: set a position for all {{ l.withoutPositionTotal.toLocaleString() }} filtered votes without one</strong>
          <select [(ngModel)]="bulkPosition"><option value="Y">YES</option><option value="N">NO</option></select>
          <label>Weight <input class="w4" type="number" min="1" max="5" [(ngModel)]="bulkWeight" /></label>
          <button (click)="bulk()">Apply</button>
          @if (bulkError()) { <span class="field-error">{{ bulkError() }}</span> }
          <span class="muted">Each change is audited per vote.</span>
        </div>
      }
      <div class="card">
        <table>
          <thead>
            <tr><th>Vote</th><th>Date</th><th>Title</th><th class="num">Country votes</th><th>Position</th><th class="num">Weight</th><th>Open conflicts</th></tr>
          </thead>
          <tbody>
            @for (v of l.items; track v.id) {
              <tr [class.row-bad]="!v.position">
                <td><a [routerLink]="['/votes/detail']" [queryParams]="{ id: v.id }">{{ v.id }}</a></td>
                <td>{{ v.date }}</td>
                <td>{{ v.title }}</td>
                <td class="num">{{ v.countryVotes }}</td>
                <td>
                  @if (v.position) { {{ v.position === 'Y' ? 'YES' : 'NO' }} } @else { <span class="pill Failed">No position</span> }
                </td>
                <td class="num">{{ v.weight ?? '—' }}</td>
                <td>
                  @if (v.pendingConflicts > 0) { <span class="pill Pending">{{ v.pendingConflicts }} pending</span> } @else { — }
                </td>
              </tr>
            }
          </tbody>
        </table>
        <div class="pager">
          <button [disabled]="l.page <= 1" (click)="load(l.page - 1)">Previous</button>
          <span class="small">Page {{ l.page }} · {{ l.total.toLocaleString() }} votes</span>
          <button [disabled]="l.page * l.pageSize >= l.total" (click)="load(l.page + 1)">Next</button>
        </div>
        <p class="muted small">Click a vote to see its country votes, set its position (admin only) and view its full history.</p>
      </div>
    }
  `,
})
export class VotesPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  readonly session_ = inject(Session);
  readonly list = signal<VoteListDto | null>(null);
  readonly sessions = signal<number[]>([]);
  readonly problem = signal<Problem | null>(null);
  readonly bulkError = signal<string | null>(null);
  session: number | null = null;
  from = '';
  to = '';
  search = '';
  withoutPosition = false;
  bulkPosition = 'Y';
  bulkWeight = 3;

  constructor() {
    this.api.invoke(apiVotesSessionsGet).then((s) => this.sessions.set(s));
    this.load(1);
  }

  async load(page: number) {
    this.problem.set(null);
    try {
      this.list.set(
        await this.api.invoke(apiVotesGet, {
          session: this.session ?? undefined,
          from: this.from || undefined,
          to: this.to || undefined,
          search: this.search || undefined,
          withoutPosition: this.withoutPosition,
          page,
          pageSize: 50,
        }),
      );
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async bulk() {
    this.bulkError.set(null);
    try {
      const r = await this.api.invoke(apiVotesPositionsBulkPost, {
        body: {
          session: this.session,
          from: this.from || null,
          to: this.to || null,
          onlyWithoutPosition: true,
          position: this.bulkPosition,
          weight: this.bulkWeight,
        },
      });
      this.toasts.show('success', `Position set on ${r.updated} vote(s).`);
      this.load(1);
    } catch (e) {
      const p = toProblem(e);
      this.bulkError.set(fieldError(p, 'Weight') ?? fieldError(p, 'Position') ?? p.detail ?? p.title);
    }
  }
}
