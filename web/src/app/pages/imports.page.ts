import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../api/api';
import { apiImportsGet, apiImportsPost } from '../api/functions';
import { ImportDto } from '../api/models';
import { Problem, fieldError, toProblem } from '../core/http';
import { Poller, ProblemComponent, Toasts, fmtDate } from '../core/ui';

/** Screen 1: upload + live progress (ING-01/02) + history of all imports (HIS-01). */
@Component({
  selector: 'app-imports',
  imports: [RouterLink, ProblemComponent],
  template: `
    <h1>Imports</h1>
    <p class="muted">Upload Assembly CSV exports. Processing runs in the background.</p>

    <div class="card row">
      <label class="dropzone" [class.over]="dragOver()" (dragover)="$event.preventDefault(); dragOver.set(true)"
        (dragleave)="dragOver.set(false)" (drop)="onDrop($event)">
        <input type="file" accept=".csv" (change)="onPick($event)" hidden />
        <strong>Drop a CSV export here</strong>
        <span class="muted small">or click to browse · .csv</span>
        @if (uploading()) {
          <span class="small">Uploading…</span>
        }
      </label>
      <div class="grow">
        @if (active().length === 0) {
          <div class="muted">Nothing is processing right now.</div>
        }
        @for (i of active(); track i.id) {
          <div class="progress-block">
            <div class="small muted">Currently processing</div>
            <strong>{{ i.fileName }}</strong>
            <div class="progress"><div [style.width.%]="i.percent"></div></div>
            <div class="small muted">
              @if (i.totalRows) {
                {{ i.processedRows.toLocaleString() }} of {{ i.totalRows.toLocaleString() }} rows · {{ i.percent }}%
              } @else {
                {{ i.phase ?? i.status }}
              }
              · started {{ fmt(i.startedAt ?? i.uploadedAt) }} by {{ i.uploadedBy }}
            </div>
          </div>
        }
      </div>
    </div>
    @if (fileError()) {
      <div class="field-error">{{ fileError() }}</div>
    }
    <app-problem [problem]="problem()" />

    <div class="card">
      <h2>Import history</h2>
      <table>
        <thead>
          <tr><th>#</th><th>File</th><th>Source</th><th>Uploaded by</th><th>Uploaded at</th><th class="num">Rows</th><th>Status</th><th></th></tr>
        </thead>
        <tbody>
          @for (i of imports(); track i.id) {
            <tr>
              <td>{{ i.id }}</td>
              <td>{{ i.fileName }}</td>
              <td class="small">{{ i.source }}</td>
              <td>{{ i.uploadedBy }}</td>
              <td>{{ fmt(i.uploadedAt) }}</td>
              <td class="num">{{ (i.totalRows ?? 0).toLocaleString() }}</td>
              <td><span class="pill" [class]="i.status">{{ statusLabel(i.status) }}</span></td>
              <td>
                @if (i.source !== 'Seed') {
                  <a [routerLink]="['/imports', i.id]">Report</a>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
      <p class="muted small">Every upload is kept: the file, who uploaded it, when, its report and the outcome of every row. Progress survives a page refresh.</p>
    </div>
  `,
})
export class ImportsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  readonly imports = signal<ImportDto[]>([]);
  readonly active = computed(() => this.imports().filter((i) => i.status === 'Queued' || i.status === 'Processing'));
  readonly uploading = signal(false);
  readonly dragOver = signal(false);
  readonly problem = signal<Problem | null>(null);
  readonly fileError = computed(() => fieldError(this.problem(), 'File'));
  readonly fmt = fmtDate;
  private readonly poll = new Poller();

  constructor() {
    // Progress lives on the server, so a refresh simply picks it up again (ING-02).
    this.poll.start(() => this.load(), 1500);
  }

  async load() {
    try {
      this.imports.set((await this.api.invoke(apiImportsGet, { pageSize: 100 })).items);
    } catch (e) {
      this.problem.set(toProblem(e));
    }
  }

  onPick(e: Event) {
    const input = e.target as HTMLInputElement;
    if (input.files?.[0]) this.upload(input.files[0]);
    input.value = '';
  }

  onDrop(e: DragEvent) {
    e.preventDefault();
    this.dragOver.set(false);
    const f = e.dataTransfer?.files?.[0];
    if (f) this.upload(f);
  }

  async upload(file: File) {
    this.problem.set(null);
    this.uploading.set(true);
    try {
      // Returns as soon as the file is stored (202); processing continues in the background.
      const dto = await this.api.invoke(apiImportsPost, { body: { File: file } });
      this.toasts.show('info', `${dto.fileName} queued (import #${dto.id}).`);
      await this.load();
    } catch (e) {
      this.problem.set(toProblem(e));
    } finally {
      this.uploading.set(false);
    }
  }

  statusLabel(s: string) {
    return s === 'CompletedWithIssues' ? 'Completed with issues' : s;
  }
}
