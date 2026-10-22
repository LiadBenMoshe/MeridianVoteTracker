import { HttpClient } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiImportsIdReportGet, apiImportsIdRowsGet } from '../api/functions';
import { ImportReportDto, ImportRowDto, RowOutcome } from '../api/models';
import { Problem, toProblem } from '../core/http';
import { Poller, ProblemComponent, fmtDate } from '../core/ui';

/** Screens 2/3: the saved import report (ING-14/15), plus every row's outcome (HIS-01). */
@Component({
  selector: 'app-import-report',
  imports: [RouterLink, FormsModule, ProblemComponent],
  template: `
    <app-problem [problem]="problem()" />
    @if (report(); as r) {
      <h1>Import report · {{ r.import.fileName }}</h1>
      <p class="muted">
        #{{ r.import.id }} · uploaded by {{ r.import.uploadedBy }} · {{ fmt(r.import.uploadedAt) }}
        @if (r.import.publishedAt) {
          · published by the Assembly {{ fmt(r.import.publishedAt) }}
        }
        · {{ (r.import.totalRows ?? 0).toLocaleString() }} rows · {{ r.import.votesInFile }} votes ·
        <a href="" (click)="$event.preventDefault(); download()">download original file</a>
      </p>

      @if (r.import.status === 'Queued' || r.import.status === 'Processing') {
        <div class="card">
          <strong>Processing…</strong> {{ r.import.processedRows.toLocaleString() }} of {{ (r.import.totalRows ?? 0).toLocaleString() }} rows
          <div class="progress"><div [style.width.%]="r.import.percent"></div></div>
        </div>
      }
      @if (r.import.status === 'Failed') {
        <div class="banner error"><strong>Import failed.</strong> {{ r.import.error }}</div>
      }
      @if (r.import.sameContentAsImportId) {
        <div class="banner info">Same file content as import #{{ r.import.sameContentAsImportId }}. Re-uploading is safe: nothing is duplicated.</div>
      }

      <div class="stats">
        <div class="stat good"><b>{{ r.import.counts.new.toLocaleString() }}</b><span>New rows</span></div>
        <div class="stat"><b>{{ r.import.counts.unchanged.toLocaleString() }}</b><span>Unchanged rows</span></div>
        <div class="stat"><b>{{ r.import.counts.duplicate }}</b><span>Duplicate rows</span></div>
        <div class="stat bad"><b>{{ r.import.counts.invalid }}</b><span>Invalid rows</span></div>
        <div class="stat warn"><b>{{ r.import.counts.contradiction }}</b><span>Contradiction rows</span></div>
        <div class="stat warn"><b>{{ r.import.counts.conflictsCreated }}</b><span>Conflicts created</span><small class="muted">{{ r.import.counts.conflict }} conflict rows</small></div>
      </div>

      @if (r.import.counts.conflictsCreated > 0) {
        <div class="banner warn">
          <strong>Approved data was not changed.</strong>
          {{ r.import.counts.conflictsCreated }} difference(s) from approved data were sent to <a routerLink="/review">Review</a>. Nothing is overwritten without a human decision.
        </div>
      }

      <div class="card">
        <h2>Per vote</h2>
        <table>
          <thead><tr><th>Vote</th><th>Title</th><th>Outcome</th><th>Details</th></tr></thead>
          <tbody>
            @for (v of r.votes; track v.voteId) {
              <tr>
                <td><a [routerLink]="['/votes/detail']" [queryParams]="{ id: v.voteId }">{{ v.voteId }}</a></td>
                <td>{{ v.title }}</td>
                <td><span class="pill" [class]="v.outcome">{{ v.outcome }}</span></td>
                <td class="small">{{ v.details }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      <div class="card">
        <h2>Rows that were not stored or need attention ({{ r.problemRowsTotal }})</h2>
        <table>
          <thead><tr><th class="num">Row</th><th>Vote</th><th>Country</th><th>Value</th><th>Outcome</th><th>Reason</th><th>Original line</th></tr></thead>
          <tbody>
            @for (row of r.problemRows; track row.rowNo) {
              <tr>
                <td class="num">{{ row.rowNo }}</td>
                <td>{{ row.voteId }}</td>
                <td>{{ row.countryCode }}</td>
                <td>{{ row.ballot }}</td>
                <td><span class="pill" [class]="row.outcome">{{ row.outcome }}</span></td>
                <td class="small">{{ row.reason }}</td>
                <td class="small mono raw">{{ row.raw }}</td>
              </tr>
            }
          </tbody>
        </table>
        @if (r.problemRowsTotal > r.problemRows.length) {
          <p class="muted small">Showing the first {{ r.problemRows.length }}. Use "All rows" below to see the rest.</p>
        }
        <p class="muted small">Valid rows were stored. Problems are reported with row number, reason and original value, never silently fixed.</p>
      </div>

      <div class="card">
        <h2>Normalisation rules (reported, not silent)</h2>
        <ul class="small">
          @for (rule of r.normalization.rules; track rule) {
            <li>{{ rule }}</li>
          }
        </ul>
        @for (a of r.normalization.applied; track a.rule) {
          <div class="small"><strong>{{ a.rule }}</strong>: {{ a.count }} value(s)
            @for (ex of a.examples.slice(0, 5); track ex.row + ex.field) {
              <span class="mono"> · row {{ ex.row }} {{ ex.field }} '{{ ex.original }}' → '{{ ex.normalized }}'</span>
            }
          </div>
        }
      </div>

      <div class="card">
        <h2>All rows</h2>
        <div class="filters">
          <select [(ngModel)]="outcome" (change)="loadRows(1)">
            <option value="">All outcomes</option>
            @for (o of outcomes; track o) {
              <option [value]="o">{{ o }}</option>
            }
          </select>
          <input placeholder="Vote id" [(ngModel)]="voteFilter" (keyup.enter)="loadRows(1)" />
          <button (click)="loadRows(1)">Filter</button>
          <span class="muted small">{{ rowsTotal().toLocaleString() }} rows</span>
        </div>
        <table>
          <thead><tr><th class="num">Row</th><th>Vote</th><th>Country</th><th>Value</th><th>Outcome</th><th>Reason</th></tr></thead>
          <tbody>
            @for (row of rows(); track row.rowNo) {
              <tr>
                <td class="num">{{ row.rowNo }}</td><td>{{ row.voteId }}</td><td>{{ row.countryCode }}</td><td>{{ row.ballot }}</td>
                <td><span class="pill" [class]="row.outcome">{{ row.outcome }}</span></td><td class="small">{{ row.reason }}</td>
              </tr>
            }
          </tbody>
        </table>
        <div class="pager">
          <button [disabled]="page() <= 1" (click)="loadRows(page() - 1)">Previous</button>
          <span class="small">Page {{ page() }}</span>
          <button [disabled]="page() * 100 >= rowsTotal()" (click)="loadRows(page() + 1)">Next</button>
        </div>
      </div>
    }
  `,
})
export class ImportReportPage {
  readonly id = input.required<string>();
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  readonly report = signal<ImportReportDto | null>(null);
  readonly problem = signal<Problem | null>(null);
  readonly rows = signal<ImportRowDto[]>([]);
  readonly rowsTotal = signal(0);
  readonly page = signal(1);
  readonly outcomes: RowOutcome[] = ['New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict'];
  outcome = '';
  voteFilter = '';
  readonly fmt = fmtDate;
  private readonly poll = new Poller();

  ngOnInit() {
    this.poll.start(() => this.load(), 1500);
    this.loadRows(1);
  }

  async load() {
    try {
      const r = await this.api.invoke(apiImportsIdReportGet, { id: +this.id() });
      this.report.set(r);
      if (r.import.status !== 'Queued' && r.import.status !== 'Processing') {
        this.poll.stop();
        this.loadRows(this.page());
      }
    } catch (e) {
      this.problem.set(toProblem(e));
      this.poll.stop();
    }
  }

  async loadRows(page: number) {
    try {
      const r = await this.api.invoke(apiImportsIdRowsGet, {
        id: +this.id(),
        page,
        pageSize: 100,
        outcome: (this.outcome || undefined) as RowOutcome | undefined,
        voteId: this.voteFilter || undefined,
      });
      this.rows.set(r.items);
      this.rowsTotal.set(r.total);
      this.page.set(page);
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  download() {
    const r = this.report();
    if (!r) return;
    this.http.get(`/api/imports/${r.import.id}/file`, { responseType: 'blob' }).subscribe((blob) => {
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = r.import.fileName;
      a.click();
      URL.revokeObjectURL(a.href);
    });
  }
}
