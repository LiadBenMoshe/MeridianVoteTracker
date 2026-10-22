import { Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiSnapshotsIdCountriesCodeGet, apiSnapshotsIdGet, apiSnapshotsPost } from '../api/functions';
import { CountryBreakdownDto, SnapshotRankingDto, SnapshotSelection } from '../api/models';
import { Problem, toProblem } from '../core/http';
import { Poller, ProblemComponent, Toasts, ballotLabel, fmtDate } from '../core/ui';

/** Screen 9: ranking + per-country breakdown with the contribution of each vote and the rules (SNP-03/04). */
@Component({
  selector: 'app-snapshot-detail',
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <app-problem [problem]="problem()" />
    @if (data(); as d) {
      <div class="row-between">
        <h1>Snapshot · {{ d.snapshot.name }}</h1>
        @if (d.snapshot.publishedQuarter) { <span class="pill Completed">PUBLISHED {{ d.snapshot.publishedQuarter }}</span> }
      </div>
      <p class="muted">
        Created by {{ d.snapshot.createdBy }} {{ fmt(d.snapshot.createdAt) }}
        @if (d.snapshot.publishedBy) { · published by {{ d.snapshot.publishedBy }} {{ fmt(d.snapshot.publishedAt) }} }
        · {{ d.snapshot.voteCount.toLocaleString() }} votes · {{ d.snapshot.selectionDescription }} · immutable · rules {{ d.snapshot.rulesVersion }}
      </p>

      @if (d.snapshot.status !== 'Completed') {
        <div class="card">
          <strong>{{ d.snapshot.status }}</strong> · {{ d.snapshot.phase }}
          <div class="progress"><div [style.width.%]="d.snapshot.progress"></div></div>
          @if (d.snapshot.error) { <div class="bad-text">{{ d.snapshot.error }}</div> }
        </div>
      } @else {
        <div class="split">
          <div class="card">
            <h2>Ranking</h2>
            <input placeholder="Find a country…" [ngModel]="filter()" (ngModelChange)="filter.set($event)" />
            <div class="scroll tall">
              <table>
                <thead><tr><th class="num">#</th><th>Member state</th><th></th><th class="num">Score</th></tr></thead>
                <tbody>
                  @for (s of filtered(); track s.code) {
                    <tr class="clickable" [class.sel]="breakdown()?.score?.code === s.code" (click)="open(s.code)">
                      <td class="num">{{ s.rank ?? '—' }}</td>
                      <td>{{ s.name }}</td>
                      <td class="barcell"><div class="bar"><div [style.width.%]="s.score ?? 0"></div></div></td>
                      <td class="num">{{ s.score === null ? 'n/a' : s.score.toFixed(1) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <div class="muted small">n/a = no countable votes in this snapshot</div>
          </div>

          <div class="card">
            @if (breakdown(); as b) {
              <h2>{{ b.score.name }} · score {{ b.score.score === null ? 'n/a' : b.score.score.toFixed(1) }} · rank {{ b.score.rank ?? '—' }} of {{ b.rankedCount }}</h2>
              <div class="muted small">
                Score = 100 × matching weights / counted weights = 100 × {{ b.score.matchedWeight }} / {{ b.score.countedWeight }}
              </div>
              <div class="rules">
                <strong>Scoring rules applied</strong>
                <ul>@for (r of b.rules; track r) { <li>{{ r }}</li> }</ul>
              </div>
              @if (b.changedSinceSnapshot > 0) {
                <div class="banner info">
                  {{ b.changedSinceSnapshot }} approved value(s) for {{ b.score.name }} changed after this snapshot was created.
                  This snapshot keeps the values approved at the time (shown in "{{ b.score.code }}"; current value in "Now").
                  <div><button class="primary" (click)="recalculate()">Create new snapshot with current data</button></div>
                </div>
              }
              <div class="scroll tall">
                <table>
                  <thead><tr><th>Vote</th><th>Position</th><th class="num">Weight</th><th>{{ b.score.code }}</th><th>Now</th><th class="num">Counted</th><th class="num">Matching</th><th>Note</th></tr></thead>
                  <tbody>
                    @for (c of b.contributions; track c.voteId) {
                      <tr [class.row-changed]="c.ballot !== c.currentBallot">
                        <td><a [routerLink]="['/votes/detail']" [queryParams]="{ id: c.voteId }">{{ c.voteId }}</a></td>
                        <td>{{ label(c.position) }}</td>
                        <td class="num">{{ c.weight }}</td>
                        <td>{{ label(c.ballot) }}</td>
                        <td class="small muted">{{ c.ballot !== c.currentBallot ? label(c.currentBallot) : '' }}</td>
                        <td class="num">{{ c.counted ? c.weight : '—' }}</td>
                        <td class="num">{{ c.matched ? c.weight : c.counted ? 0 : '—' }}</td>
                        <td class="small muted">{{ c.note }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <h2>Scoring rules</h2>
              <ul class="small">@for (r of d.snapshot.rules; track r) { <li>{{ r }}</li> }</ul>
              <div class="muted">Select a country to see how its score was calculated.</div>
            }
          </div>
        </div>
      }
    }
  `,
})
export class SnapshotDetailPage {
  readonly id = input.required<string>();
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  readonly data = signal<SnapshotRankingDto | null>(null);
  readonly breakdown = signal<CountryBreakdownDto | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly filter = signal('');
  readonly fmt = fmtDate;
  readonly label = ballotLabel;
  private readonly poll = new Poller();

  readonly filtered = computed(() => {
    const f = this.filter().trim().toLowerCase();
    const scores = this.data()?.scores ?? [];
    return f ? scores.filter((s) => (s.name + ' ' + s.code).toLowerCase().includes(f)) : scores;
  });

  ngOnInit() {
    this.poll.start(() => this.load(), 1500);
  }

  async load() {
    try {
      const d = await this.api.invoke(apiSnapshotsIdGet, { id: +this.id() });
      this.data.set(d);
      if (d.snapshot.status === 'Completed' || d.snapshot.status === 'Failed') this.poll.stop();
    } catch (e) {
      this.problem.set(toProblem(e));
      this.poll.stop();
    }
  }

  async open(code: string) {
    try {
      this.breakdown.set(await this.api.invoke(apiSnapshotsIdCountriesCodeGet, { id: +this.id(), code }));
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async recalculate() {
    // Same votes as this snapshot, recalculated with today's approved data and positions (SNP-04).
    const d = this.data()!;
    const selection: SnapshotSelection = { voteIds: this.breakdown()!.contributions.map((c) => c.voteId) };
    try {
      const s = await this.api.invoke(apiSnapshotsPost, { body: { name: `${d.snapshot.name} (recalculated)`, selection } });
      this.toasts.show('info', 'New snapshot is being calculated with the current approved data.');
      this.breakdown.set(null);
      await this.router.navigate(['/snapshots', s.id]);
      this.poll.start(() => this.load(), 1500);
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }
}
