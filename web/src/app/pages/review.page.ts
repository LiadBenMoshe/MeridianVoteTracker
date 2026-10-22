import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../api/api';
import {
  apiConflictsGet,
  apiConflictsIdDecisionPost,
  apiExtractionsGet,
  apiExtractionsIdApprovePost,
  apiExtractionsIdGet,
  apiExtractionsIdPut,
  apiExtractionsIdRejectPost,
  apiExtractionsIdRetryPost,
  apiExtractionsPost,
} from '../api/functions';
import { ConflictDto, ExtractedRow, ExtractedSheet, ExtractionDto } from '../api/models';
import { Problem, toProblem } from '../core/http';
import { Poller, ProblemComponent, Toasts, ballotLabel, fmtDate } from '../core/ui';

type Tab = 'conflicts' | 'extractions' | 'resolved';

/** Screens 4/5 (+10 bonus): one inbox of items waiting for a human decision (CON-01..06, PDF-01..06). */
@Component({
  selector: 'app-review',
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <h1>Review</h1>
    <p class="muted">Items waiting for a human decision.</p>
    <div class="split">
      <div class="card list-pane">
        <input placeholder="Filter by vote…" [(ngModel)]="voteFilter" (keyup)="loadList()" />
        <div class="tabs">
          <button [class.on]="tab() === 'conflicts'" (click)="setTab('conflicts')">Conflicts {{ pendingTotal() }}</button>
          <button [class.on]="tab() === 'extractions'" (click)="setTab('extractions')">Extractions {{ extractionsToReview() }}</button>
          <button [class.on]="tab() === 'resolved'" (click)="setTab('resolved')">Resolved {{ resolvedTotal() }}</button>
        </div>
        @if (tab() !== 'extractions') {
          @for (c of conflicts(); track c.id) {
            <div class="item" [class.sel]="selected()?.id === c.id" (click)="select(c)">
              <div class="row-between"><strong>{{ c.voteId }}</strong><span class="pill" [class]="c.status">{{ c.status }}</span></div>
              <div class="small muted">{{ describe(c) }}</div>
            </div>
          } @empty {
            <div class="muted small">Nothing here.</div>
          }
          @if (conflictsTotal() > conflicts().length) {
            <div class="muted small">Showing {{ conflicts().length }} of {{ conflictsTotal() }}. Filter by vote to narrow.</div>
          }
        } @else {
          <label class="upload-btn">
            <input type="file" accept=".pdf" hidden (change)="uploadPdf($event)" />
            Upload scanned PDF
          </label>
          @for (e of extractions(); track e.id) {
            <div class="item" [class.sel]="extraction()?.id === e.id" (click)="openExtraction(e.id)">
              <div class="row-between"><strong>{{ e.fileName }}</strong><span class="pill" [class]="e.status">{{ extractionLabel(e) }}</span></div>
              <div class="small muted">{{ e.sheet?.voteId ?? '—' }} · {{ fmt(e.createdAt) }}</div>
              @if (e.issueCount > 0 && e.status === 'NeedsReview') {
                <div class="small bad-text">{{ e.issueCount }} issue(s) found</div>
              }
            </div>
          } @empty {
            <div class="muted small">No extractions yet.</div>
          }
        }
      </div>

      <div class="card detail-pane">
        <app-problem [problem]="problem()" />
        @if (tab() !== 'extractions') {
          @if (selected(); as c) {
            @if (lost(); as l) {
              <div class="banner warn">
                <strong>{{ l.title }}</strong>
                <div>{{ l.detail }} The current state is shown below.</div>
              </div>
            }
            <div class="row-between">
              <h2>{{ c.kind === 'CountryVote' ? 'Country vote conflict' : 'Vote detail conflict' }} · {{ c.voteId }}
                @if (c.countryCode) { · {{ c.countryName }} ({{ c.countryCode }}) }
              </h2>
              <span class="pill" [class]="c.status">{{ c.status }}</span>
            </div>
            <div class="muted small">
              <a [routerLink]="['/votes/detail']" [queryParams]="{ id: c.voteId }">{{ c.voteTitle }}</a> · Session {{ c.session }} · {{ c.voteDate }}
              @if (c.kind === 'VoteDetail') { · field: {{ c.field }} }
            </div>

            @if (c.status === 'Pending') {
              <div class="compare">
                <div class="box approved">
                  <div class="label">APPROVED VALUE</div>
                  <div class="value">{{ show(c, c.currentApprovedValue) }}</div>
                  @if (c.currentApprovedValue !== c.approvedValue) {
                    <div class="small bad-text">Changed since this conflict was opened (was {{ show(c, c.approvedValue) }})</div>
                  }
                  <div class="small">
                    <strong>{{ c.approvedSource?.fileName ?? '—' }}</strong><br />
                    uploaded {{ fmt(c.approvedSource?.uploadedAt) }} by {{ c.approvedSource?.uploadedBy }}<br />
                    published by the Assembly {{ fmt(c.approvedSource?.publishedAt) }}
                    @if (c.approvedSource?.approvedBy && c.approvedSource?.approvedBy !== 'system') {
                      <br />approved by {{ c.approvedSource?.approvedBy }}
                    }
                  </div>
                </div>
                <div class="box incoming">
                  <div class="label">INCOMING VALUE</div>
                  <div class="value">{{ show(c, c.incomingValue) }}</div>
                  <div class="small">
                    <strong>{{ c.incomingSource.fileName }}</strong><br />
                    uploaded {{ fmt(c.incomingSource.uploadedAt) }} by {{ c.incomingSource.uploadedBy }}<br />
                    published by the Assembly {{ fmt(c.incomingSource.publishedAt) }}
                  </div>
                </div>
              </div>
              <h3>Impact</h3>
              <ul class="small">
                @for (i of c.impact; track i) { <li>{{ i }}</li> }
              </ul>
              <label class="field">Comment (optional)
                <textarea rows="3" [(ngModel)]="comment"></textarea>
              </label>
              <div class="actions">
                <button class="primary green" [disabled]="busy()" (click)="decide(c, 'Accept')">Accept incoming value</button>
                <button class="danger-outline" [disabled]="busy()" (click)="decide(c, 'Reject')">Reject, keep approved</button>
              </div>
              <p class="muted small">Every decision is recorded with who decided, when and the comment.</p>
            } @else {
              <div class="compare">
                <div class="box approved">
                  <div class="label">APPROVED VALUE (CURRENT)</div>
                  <div class="value">{{ show(c, c.currentApprovedValue) }}</div>
                  <div class="small">{{ c.status === 'Accepted' ? 'accepted' : 'kept' }} by {{ c.decidedBy }}<br />{{ fmt(c.decidedAt) }}</div>
                </div>
                <div class="box previous">
                  <div class="label">{{ c.status === 'Accepted' ? 'PREVIOUS VALUE' : 'REJECTED VALUE' }}</div>
                  <div class="value">{{ show(c, c.status === 'Accepted' ? (c.valueBeforeDecision ?? c.approvedValue) : c.incomingValue) }}</div>
                  <div class="small">{{ c.status === 'Accepted' ? 'replaced by ' + c.decidedBy + "'s decision, kept in the vote history" : 'from ' + c.incomingSource.fileName }}</div>
                </div>
              </div>
              @if (c.comment) {
                <p><strong>{{ c.decidedBy }}'s comment</strong><br />"{{ c.comment }}"</p>
              }
              <div class="actions">
                <button disabled>Accept incoming value</button>
                <button disabled>Reject, keep approved</button>
                <button class="primary" (click)="openNext()">Open next item</button>
              </div>
              <p class="muted small">Exactly one decision takes effect. Nothing is overwritten silently.</p>
            }
          } @else {
            <div class="muted">Select an item on the left.</div>
          }
        } @else {
          @if (extraction(); as e) {
            <div class="row-between">
              <h2>Extraction · {{ e.fileName }} · {{ e.sheet?.pages ?? '?' }} page(s)</h2>
              <span class="pill" [class]="e.status">{{ extractionLabel(e) }}</span>
            </div>
            @if (!e.aiConfigured) {
              <div class="banner warn">Azure Document Intelligence is not configured on the server (see README).</div>
            }
            @if (e.status === 'Queued' || e.status === 'Running') {
              <div class="banner info">Extracting with Azure Document Intelligence… attempt {{ e.attempts }}.
                @if (e.error) { <div class="small">Last error: {{ e.error }} — retrying automatically.</div> }
              </div>
            }
            @if (e.status === 'Failed') {
              <div class="banner error"><strong>Extraction failed.</strong> {{ e.error }}
                <div><button (click)="retry(e)">Retry</button></div>
              </div>
            }
            @if (sheet(); as s) {
              <div class="grid3">
                <label class="field">Vote ID <input [(ngModel)]="s.voteId" [disabled]="!editable()" /></label>
                <label class="field">Date (yyyy-mm-dd) <input [(ngModel)]="s.date" [disabled]="!editable()" /></label>
                <label class="field">Session <input [(ngModel)]="s.session" [disabled]="!editable()" /></label>
              </div>
              <label class="field">Title <input [(ngModel)]="s.title" [disabled]="!editable()" /></label>

              @if ((s.issues ?? []).length) {
                <div class="banner error">
                  @for (i of s.issues; track i) { <div>{{ i }}</div> }
                </div>
              }
              <div class="filters">
                <label class="small"><input type="checkbox" [(ngModel)]="onlyIssues" /> Only rows with issues ({{ rowsWithIssues() }})</label>
                <span class="muted small">{{ s.rows?.length }} rows · edit any value before approving</span>
              </div>
              <div class="scroll">
                <table>
                  <thead><tr><th>Code</th><th>Member state</th><th>Extracted</th><th class="num">Confidence</th><th>Issue</th><th></th></tr></thead>
                  <tbody>
                    @for (r of visibleRows(); track $index) {
                      <tr [class.row-bad]="(r.issues ?? []).length > 0" [class.row-low]="(r.confidence ?? 1) < 0.8">
                        <td><input class="w4" [(ngModel)]="r.code" [disabled]="!editable()" /></td>
                        <td>{{ r.name }}</td>
                        <td>
                          <select [(ngModel)]="r.ballot" [disabled]="!editable()">
                            <option [ngValue]="null">?</option>
                            <option value="Y">YES</option><option value="N">NO</option><option value="A">ABSTAIN</option><option value="X">ABSENT</option>
                          </select>
                        </td>
                        <td class="num">{{ (r.confidence ?? 1).toFixed(2) }}</td>
                        <td class="small bad-text">{{ (r.issues ?? []).join(', ') }}</td>
                        <td>
                          @if (editable() && (r.confidence ?? 1) < 1) {
                            <button class="small-btn" (click)="confirmRow(r)" title="I checked this row on the scan">Confirm</button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <div class="small">
                Printed totals:
                YES <input class="w4" type="number" [(ngModel)]="s.totals!.yes" [disabled]="!editable()" />
                NO <input class="w4" type="number" [(ngModel)]="s.totals!.no" [disabled]="!editable()" />
                ABSTAIN <input class="w4" type="number" [(ngModel)]="s.totals!.abstain" [disabled]="!editable()" />
                ABSENT <input class="w4" type="number" [(ngModel)]="s.totals!.notVoting" [disabled]="!editable()" />
              </div>
              @if (editable()) {
                <label class="field">Comment (optional) <input [(ngModel)]="comment" /></label>
                <div class="actions">
                  <button class="primary green" [disabled]="busy() || e.issueCount > 0" (click)="approve(e)">Approve</button>
                  <button [disabled]="busy()" (click)="saveEdits(e)">Save edits & re-check</button>
                  <button class="danger-outline" [disabled]="busy()" (click)="reject(e)">Reject</button>
                </div>
                <p class="muted small">Resolve the flagged issues before approving. Approved extractions follow the same rules as CSV ingestion: new, unchanged or conflict.</p>
              } @else if (e.status === 'Approved') {
                <div class="banner info">Approved by {{ e.reviewedBy }} {{ fmt(e.reviewedAt) }} → <a [routerLink]="['/imports', e.importId]">import #{{ e.importId }}</a></div>
              } @else if (e.status === 'Rejected') {
                <div class="banner info">Rejected by {{ e.reviewedBy }} {{ fmt(e.reviewedAt) }}. {{ e.reviewComment }}</div>
              }
            }
          } @else {
            <div class="muted">Upload a scanned voting sheet or select an extraction.</div>
          }
        }
      </div>
    </div>
  `,
})
export class ReviewPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  readonly tab = signal<Tab>('conflicts');
  readonly conflicts = signal<ConflictDto[]>([]);
  readonly conflictsTotal = signal(0);
  readonly pendingTotal = signal(0);
  readonly resolvedTotal = signal(0);
  readonly selected = signal<ConflictDto | null>(null);
  readonly lost = signal<Problem | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly busy = signal(false);
  readonly extractions = signal<ExtractionDto[]>([]);
  readonly extraction = signal<ExtractionDto | null>(null);
  readonly sheet = signal<ExtractedSheet | null>(null);
  readonly extractionsToReview = computed(() => this.extractions().filter((e) => e.status === 'NeedsReview').length);
  readonly editable = computed(() => this.extraction()?.status === 'NeedsReview');
  voteFilter = '';
  comment = '';
  onlyIssues = false;
  readonly fmt = fmtDate;
  private readonly poll = new Poller();

  constructor() {
    this.loadList();
    this.loadExtractions();
    // Extraction status is shown while it runs in the background (PDF-01).
    this.poll.start(() => this.refreshRunning(), 2000);
  }

  setTab(t: Tab) {
    this.tab.set(t);
    this.selected.set(null);
    this.lost.set(null);
    this.problem.set(null);
    if (t === 'extractions') this.loadExtractions();
    else this.loadList();
  }

  async loadList() {
    try {
      const voteId = this.voteFilter || undefined;
      // Each tab keeps its own counter: pending conflicts and resolved conflicts.
      const [pending, resolved] = await Promise.all([
        this.api.invoke(apiConflictsGet, { voteId, resolved: false, pageSize: 200 }),
        this.api.invoke(apiConflictsGet, { voteId, resolved: true, pageSize: 200 }),
      ]);
      this.pendingTotal.set(pending.total);
      this.resolvedTotal.set(resolved.total);
      if (this.tab() === 'extractions') return;
      const r = this.tab() === 'resolved' ? resolved : pending;
      this.conflicts.set(r.items);
      this.conflictsTotal.set(r.total);
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  select(c: ConflictDto) {
    this.selected.set(c);
    this.lost.set(null);
    this.problem.set(null);
    this.comment = '';
  }

  async decide(c: ConflictDto, decision: 'Accept' | 'Reject') {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const r = await this.api.invoke(apiConflictsIdDecisionPost, { id: c.id, body: { decision, comment: this.comment || null } });
      this.selected.set(r);
      this.toasts.show('success', `Conflict ${decision === 'Accept' ? 'accepted' : 'rejected'}.`);
    } catch (e) {
      const p = toProblem(e);
      if (p.status === 409 && p.body['current']) {
        // CON-05: someone else decided first. Show a clear message and the current state.
        this.lost.set(p);
        this.selected.set(p.body['current'] as ConflictDto);
      } else {
        this.problem.set(p);
      }
    } finally {
      this.busy.set(false);
      this.loadList();
    }
  }

  openNext() {
    const next = this.conflicts().find((c) => c.status === 'Pending' && c.id !== this.selected()?.id);
    if (next) this.select(next);
    else this.selected.set(null);
  }

  describe(c: ConflictDto) {
    return c.kind === 'CountryVote'
      ? `${c.countryCode} country vote: ${ballotLabel(c.approvedValue)} → ${ballotLabel(c.incomingValue)}`
      : `Vote ${c.field.replace('resolution_', '').replace('vote_', '')}: changed`;
  }

  show(c: ConflictDto, v: string | null | undefined) {
    return c.kind === 'CountryVote' ? ballotLabel(v) : v ?? '—';
  }

  // ---------- extractions (bonus) ----------

  async loadExtractions() {
    try {
      this.extractions.set(await this.api.invoke(apiExtractionsGet, {}));
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async refreshRunning() {
    const running = this.extractions().some((e) => e.status === 'Queued' || e.status === 'Running');
    if (!running) return;
    await this.loadExtractions();
    const cur = this.extraction();
    if (cur && (cur.status === 'Queued' || cur.status === 'Running')) this.openExtraction(cur.id);
  }

  async uploadPdf(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    try {
      const e = await this.api.invoke(apiExtractionsPost, { body: { File: file } });
      this.toasts.show('info', `${e.fileName} uploaded. Extraction is running in the background.`);
      await this.loadExtractions();
      this.openExtraction(e.id);
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  async openExtraction(id: number) {
    this.problem.set(null);
    try {
      const e = await this.api.invoke(apiExtractionsIdGet, { id });
      this.extraction.set(e);
      const s = e.sheet ? (JSON.parse(JSON.stringify(e.sheet)) as ExtractedSheet) : null;
      if (s && !s.totals) s.totals = { yes: null, no: null, abstain: null, notVoting: null };
      this.sheet.set(s);
    } catch (err) {
      this.problem.set(toProblem(err));
    }
  }

  visibleRows(): ExtractedRow[] {
    const rows = this.sheet()?.rows ?? [];
    return this.onlyIssues ? rows.filter((r) => (r.issues ?? []).length > 0) : rows;
  }

  rowsWithIssues() {
    return (this.sheet()?.rows ?? []).filter((r) => (r.issues ?? []).length > 0).length;
  }

  confirmRow(r: ExtractedRow) {
    r.confidence = 1;
  }

  async saveEdits(e: ExtractionDto) {
    await this.run(async () => {
      const updated = await this.api.invoke(apiExtractionsIdPut, { id: e.id, body: { sheet: this.sheet()!, version: e.version } });
      this.toasts.show('info', updated.issueCount === 0 ? 'Saved. No issues left.' : `Saved. ${updated.issueCount} issue(s) left.`);
      await this.openExtraction(e.id);
    });
  }

  async approve(e: ExtractionDto) {
    await this.run(async () => {
      const r = await this.api.invoke(apiExtractionsIdApprovePost, { id: e.id, body: { version: e.version, comment: this.comment || null } });
      this.toasts.show('success', `Approved. Imported as import #${r.importId} with the normal rules.`);
      await this.openExtraction(e.id);
    });
  }

  async reject(e: ExtractionDto) {
    await this.run(async () => {
      await this.api.invoke(apiExtractionsIdRejectPost, { id: e.id, body: { version: e.version, comment: this.comment || null } });
      await this.openExtraction(e.id);
    });
  }

  async retry(e: ExtractionDto) {
    await this.run(async () => {
      await this.api.invoke(apiExtractionsIdRetryPost, { id: e.id });
      await this.openExtraction(e.id);
    });
  }

  private async run(f: () => Promise<void>) {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await f();
    } catch (err) {
      const p = toProblem(err);
      this.problem.set(p);
      if (p.status === 409 && this.extraction()) await this.openExtraction(this.extraction()!.id);
    } finally {
      this.busy.set(false);
      this.loadExtractions();
    }
  }

  extractionLabel(e: ExtractionDto) {
    return e.status === 'NeedsReview' ? 'Needs review' : e.status;
  }
}
