import { Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiVotesDetailGet, apiVotesPositionPut, apiVotesTimelineGet } from '../api/functions';
import { TimelineEventDto, VoteDetailDto } from '../api/models';
import { Problem, fieldError, toProblem } from '../core/http';
import { Session } from '../core/session';
import { ProblemComponent, Toasts, ballotLabel, fmtDate } from '../core/ui';

/** Screen 7: one vote — position & weight (POS-01/02), country votes, full timeline (HIS-02..04). */
@Component({
  selector: 'app-vote-detail',
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <app-problem [problem]="problem()" />
    @if (vote(); as v) {
      <div class="row-between">
        <h1>{{ v.id }} · {{ v.title }}</h1>
        @if (v.pendingConflicts > 0) {
          <a routerLink="/review" class="pill Pending">{{ v.pendingConflicts }} pending conflict(s)</a>
        }
      </div>
      <p class="muted">
        Session {{ v.session }} · {{ v.date }} · {{ v.countryVotes.length }} country votes ·
        YES {{ v.tallies.yes }} · NO {{ v.tallies.no }} · ABSTAIN {{ v.tallies.abstain }} · ABSENT {{ v.tallies.absent }}
        @if (v.detailsSource) { · details from {{ v.detailsSource.fileName }} }
      </p>

      <div class="split">
        <div>
          <div class="card">
            <h2>Northwind position</h2>
            <div class="small muted">Desired outcome</div>
            <div class="toggle">
              <button [class.on]="position === 'Y'" [disabled]="!session.isAdmin()" (click)="position = 'Y'">YES</button>
              <button [class.on]="position === 'N'" [disabled]="!session.isAdmin()" (click)="position = 'N'">NO</button>
            </div>
            @if (positionError()) { <div class="field-error">{{ positionError() }}</div> }
            <label class="field">Weight (1–5)
              <input type="number" class="w6" [class.invalid]="!!weightError()" [(ngModel)]="weight" [disabled]="!session.isAdmin()" />
            </label>
            @if (weightError()) { <div class="field-error">{{ weightError() }}</div> }
            <div class="actions">
              <button class="primary" [disabled]="!session.isAdmin() || saving()" (click)="save()">Save position</button>
              <span class="muted small">
                @if (session.isAdmin()) { Every change is recorded with the old and new value. } @else { Only admins can change positions (enforced by the API). }
              </span>
            </div>
            @if (v.positionUpdatedBy) {
              <div class="muted small">Last changed by {{ v.positionUpdatedBy }} · {{ fmt(v.positionUpdatedAt) }}</div>
            }
          </div>

          <div class="card">
            <h2>Country votes</h2>
            <input placeholder="Filter by code or name…" [(ngModel)]="countryFilter" />
            <div class="scroll">
              <table>
                <thead><tr><th>Code</th><th>Member state</th><th>Choice</th><th>Source</th></tr></thead>
                <tbody>
                  @for (c of filteredCountries(); track c.code) {
                    <tr [class.row-changed]="c.changedByDecision">
                      <td>{{ c.code }}</td>
                      <td>{{ c.name }}</td>
                      <td>{{ label(c.ballot) }} @if (c.changedByDecision) { <span title="changed by a conflict decision">●</span> }</td>
                      <td class="small muted">{{ c.sourceFile }} @if (c.changedByDecision) { · approved by {{ c.approvedBy }} }</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <div class="muted small">● changed by a conflict decision</div>
          </div>
        </div>

        <div class="card">
          <h2>History of this vote</h2>
          <ul class="timeline">
            @for (e of timeline(); track e.id) {
              <li [class]="e.type">
                <div class="row-between"><strong>{{ title(e) }}</strong><span class="muted small">{{ fmt(e.at) }} · {{ e.actor }}</span></div>
                <div class="small muted">{{ text(e) }}</div>
              </li>
            }
          </ul>
        </div>
      </div>
    }
  `,
})
export class VoteDetailPage {
  /** Vote id comes from ?id=MA/2024/003 (ids contain '/'). */
  readonly id = input.required<string>();
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  readonly session = inject(Session);
  readonly vote = signal<VoteDetailDto | null>(null);
  readonly timeline = signal<TimelineEventDto[]>([]);
  readonly problem = signal<Problem | null>(null);
  readonly saveProblem = signal<Problem | null>(null);
  readonly saving = signal(false);
  readonly weightError = computed(() => fieldError(this.saveProblem(), 'Weight'));
  readonly positionError = computed(() => fieldError(this.saveProblem(), 'Position'));
  position: string | null = null;
  weight: number | null = null;
  countryFilter = '';
  readonly fmt = fmtDate;
  readonly label = ballotLabel;

  ngOnInit() {
    this.load();
  }

  filteredCountries() {
    const f = this.countryFilter.trim().toLowerCase();
    const list = this.vote()?.countryVotes ?? [];
    return f ? list.filter((c) => c.code.toLowerCase().includes(f) || c.name.toLowerCase().includes(f)) : list;
  }

  async load() {
    try {
      const v = await this.api.invoke(apiVotesDetailGet, { id: this.id() });
      this.vote.set(v);
      this.position = v.position;
      this.weight = v.weight;
      this.timeline.set(await this.api.invoke(apiVotesTimelineGet, { id: this.id() }));
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async save() {
    this.saving.set(true);
    this.saveProblem.set(null);
    try {
      await this.api.invoke(apiVotesPositionPut, { id: this.id(), body: { position: this.position as string, weight: this.weight as number } });
      this.toasts.show('success', 'Position saved.');
      await this.load();
    } catch (e) {
      const p = toProblem(e);
      // Validation errors (e.g. weight 7) go next to the field; anything else as a banner.
      if (p.status === 400 && Object.keys(p.errors).length) this.saveProblem.set(p);
      else this.problem.set(p);
    } finally {
      this.saving.set(false);
    }
  }

  title(e: TimelineEventDto) {
    return (
      {
        VoteCreated: 'Vote created',
        CountryVotesAdded: 'Country votes added',
        ConflictsCreated: 'Conflicts created',
        ConflictAccepted: 'Conflict accepted',
        ConflictRejected: 'Conflict rejected',
        DecisionNotApplied: 'Decision not applied',
        PositionChanged: e.field === 'weight' ? 'Weight changed' : 'Position changed',
        SnapshotPublished: 'Snapshot published',
      } as Record<string, string>
    )[e.type] ?? e.type;
  }

  text(e: TimelineEventDto) {
    if (e.type === 'ConflictAccepted' || e.type === 'ConflictRejected') {
      const what = e.countryCode ? `${e.countryCode}: ${ballotLabel(e.oldValue)} → ${ballotLabel(e.newValue)}` : `${e.field}: "${e.oldValue}" → "${e.newValue}"`;
      return `${what} · source: ${e.importFile ?? '—'} · ${e.message ?? ''}`;
    }
    return e.message ?? '';
  }
}
