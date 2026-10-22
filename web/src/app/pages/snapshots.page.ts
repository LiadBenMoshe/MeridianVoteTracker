import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiSnapshotsGet, apiSnapshotsIdPublishPost, apiSnapshotsPost, apiSnapshotsPreviewPost } from '../api/functions';
import { SnapshotDto, SnapshotPreviewDto, SnapshotSelection } from '../api/models';
import { Problem, fieldError, toProblem } from '../core/http';
import { Session } from '../core/session';
import { Poller, ProblemComponent, Toasts, fmtDate } from '../core/ui';

type Mode = 'range' | 'session' | 'list';

/** Screen 8: create a snapshot (SNP-01, POS-04), list with background status (SNP-02), publish (SNP-05). */
@Component({
  selector: 'app-snapshots',
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <h1>Snapshots</h1>
    <p class="muted">Saved calculations of the Alignment Index. A saved snapshot never changes.</p>
    <div class="split">
      <div class="card">
        <h2>Create snapshot</h2>
        <label class="field">Name <input [(ngModel)]="name" [class.invalid]="!!nameError()" /></label>
        @if (nameError()) { <div class="field-error">{{ nameError() }}</div> }

        <div class="small muted">Select votes</div>
        <div class="radios">
          <label><input type="radio" name="mode" value="range" [(ngModel)]="mode" (change)="preview()" /> Date range</label>
          <label><input type="radio" name="mode" value="session" [(ngModel)]="mode" (change)="preview()" /> Session</label>
          <label><input type="radio" name="mode" value="list" [(ngModel)]="mode" (change)="preview()" /> List</label>
        </div>
        @switch (mode) {
          @case ('range') {
            <div class="grid2">
              <label class="field">From <input type="date" [(ngModel)]="from" (change)="preview()" /></label>
              <label class="field">To <input type="date" [(ngModel)]="to" (change)="preview()" /></label>
            </div>
          }
          @case ('session') {
            <label class="field">Session <input type="number" [(ngModel)]="sessionNo" (change)="preview()" /></label>
          }
          @case ('list') {
            <label class="field">Vote IDs (comma or new line)
              <textarea rows="4" [(ngModel)]="list" (blur)="preview()"></textarea>
            </label>
          }
        }
        @if (selectionError()) { <div class="field-error">{{ selectionError() }}</div> }

        @if (previewData(); as p) {
          <div><strong>{{ p.voteCount.toLocaleString() }} votes selected</strong></div>
          @if (p.unknownVoteIds.length) {
            <div class="field-error">Unknown vote id(s): {{ p.unknownVoteIds.join(', ') }}</div>
          }
          @if (p.missingPositionTotal > 0) {
            <div class="banner error">
              <strong>{{ p.missingPositionTotal }} selected vote(s) have no position</strong>
              @for (v of p.missingPosition.slice(0, 8); track v.id) {
                <div class="small"><a [routerLink]="['/votes/detail']" [queryParams]="{ id: v.id }">{{ v.id }}</a> · {{ v.title }}</div>
              }
              <div class="small">Set a position (admin) or remove them from the selection.</div>
              @if (mode === 'list') {
                <button (click)="removeMissing(p)">Remove {{ p.missingPositionTotal }} vote(s) from the list</button>
              }
            </div>
          }
        }
        <app-problem [problem]="problem()" />
        <button class="primary" [disabled]="!canCreate() || busy()" (click)="create()">Create snapshot</button>
        <p class="muted small">The calculation runs in the background. Rules for abstain/absent/non-members are shown in each snapshot.</p>
      </div>

      <div class="card">
        <h2>All snapshots</h2>
        <table>
          <thead><tr><th>Name</th><th class="num">Votes</th><th>Created</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (s of snapshots(); track s.id) {
              <tr>
                <td><a [routerLink]="['/snapshots', s.id]">{{ s.name }}</a><div class="small muted">{{ s.selectionDescription }}</div></td>
                <td class="num">{{ s.voteCount.toLocaleString() }}</td>
                <td class="small">{{ s.createdBy }} · {{ fmt(s.createdAt) }}</td>
                <td>
                  @if (s.publishedQuarter) {
                    <span class="pill Completed">Published {{ s.publishedQuarter }}</span>
                  } @else if (s.status === 'Completed') {
                    <span class="pill">Saved</span>
                  } @else {
                    <span class="pill" [class]="s.status">{{ s.status }}</span>
                    @if (s.status === 'Running' || s.status === 'Queued') {
                      <div class="progress small-bar"><div [style.width.%]="s.progress"></div></div>
                      <div class="small muted">{{ s.phase }}</div>
                    }
                    @if (s.error) { <div class="small bad-text">{{ s.error }}</div> }
                  }
                </td>
                <td>
                  @if (session.isAdmin() && s.status === 'Completed' && !s.publishedQuarter) {
                    <div class="publish">
                      <input class="w8" [placeholder]="s.suggestedQuarter ?? '2024-Q1'" [(ngModel)]="quarters[s.id]" />
                      <button (click)="publish(s)">Mark as published</button>
                    </div>
                    @if (publishErrors()[s.id]) { <div class="field-error">{{ publishErrors()[s.id] }}</div> }
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
        <p class="muted small">Admins can mark one snapshot per quarter as published.</p>
      </div>
    </div>
  `,
})
export class SnapshotsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  readonly session = inject(Session);
  readonly snapshots = signal<SnapshotDto[]>([]);
  readonly previewData = signal<SnapshotPreviewDto | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly formProblem = signal<Problem | null>(null);
  readonly busy = signal(false);
  readonly publishErrors = signal<Record<number, string>>({});
  readonly nameError = computed(() => fieldError(this.formProblem(), 'Name'));
  readonly selectionError = computed(
    () => fieldError(this.formProblem(), 'Selection') ?? fieldError(this.formProblem(), 'Selection.VoteIds') ?? fieldError(this.formProblem(), 'Selection.To'),
  );
  readonly canCreate = computed(() => {
    const p = this.previewData();
    return !!p && p.voteCount > 0 && p.missingPositionTotal === 0 && p.unknownVoteIds.length === 0;
  });
  name = '';
  mode: Mode = 'range';
  from = '2024-01-01';
  to = '2024-03-31';
  sessionNo: number | null = 79;
  list = '';
  quarters: Record<number, string> = {};
  readonly fmt = fmtDate;
  private readonly poll = new Poller();

  constructor() {
    const q = inject(ActivatedRoute).snapshot.queryParamMap;
    if (q.get('from')) this.from = q.get('from')!;
    if (q.get('to')) this.to = q.get('to')!;
    if (q.get('name')) this.name = q.get('name')!;
    this.poll.start(() => this.load(), 2000);
    this.preview();
  }

  selection(): SnapshotSelection {
    switch (this.mode) {
      case 'range':
        return { from: this.from || null, to: this.to || null };
      case 'session':
        return { session: this.sessionNo };
      default:
        return { voteIds: this.list.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean) };
    }
  }

  async load() {
    try {
      this.snapshots.set(await this.api.invoke(apiSnapshotsGet));
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async preview() {
    this.formProblem.set(null);
    try {
      this.previewData.set(await this.api.invoke(apiSnapshotsPreviewPost, { body: this.selection() }));
    } catch (e) {
      this.previewData.set(null);
      this.formProblem.set(toProblem(e));
    }
  }

  removeMissing(p: SnapshotPreviewDto) {
    const missing = new Set(p.missingPosition.map((v) => v.id));
    this.list = this.selection().voteIds!.filter((id) => !missing.has(id)).join('\n');
    this.preview();
  }

  async create() {
    this.busy.set(true);
    this.problem.set(null);
    this.formProblem.set(null);
    try {
      const s = await this.api.invoke(apiSnapshotsPost, { body: { name: this.name, selection: this.selection() } });
      this.toasts.show('info', `Snapshot "${s.name}" is being calculated in the background.`);
      await this.load();
    } catch (e) {
      const p = toProblem(e);
      if (p.status === 400) this.formProblem.set(p);
      else this.problem.set(p); // e.g. 422 votes without position
    } finally {
      this.busy.set(false);
    }
  }

  async publish(s: SnapshotDto) {
    const quarter = this.quarters[s.id] || s.suggestedQuarter || '';
    this.publishErrors.update((m) => ({ ...m, [s.id]: '' }));
    try {
      await this.api.invoke(apiSnapshotsIdPublishPost, { id: s.id, body: { quarter } });
      this.toasts.show('success', `"${s.name}" published as ${quarter}.`);
      await this.load();
    } catch (e) {
      const p = toProblem(e);
      this.publishErrors.update((m) => ({ ...m, [s.id]: fieldError(p, 'Quarter') ?? p.detail ?? p.title }));
    }
  }
}
