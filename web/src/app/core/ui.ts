import { Component, DestroyRef, Injectable, inject, input, signal } from '@angular/core';
import { Problem } from './http';

export interface Toast {
  id: number;
  kind: 'error' | 'info' | 'success';
  text: string;
  correlationId?: string;
}

@Injectable({ providedIn: 'root' })
export class Toasts {
  readonly items = signal<Toast[]>([]);
  private next = 1;

  show(kind: Toast['kind'], text: string, correlationId?: string) {
    const t = { id: this.next++, kind, text, correlationId };
    this.items.update((l) => [...l, t]);
    setTimeout(() => this.dismiss(t.id), kind === 'error' ? 10000 : 4000);
  }

  problem(p: Problem) {
    this.show('error', p.detail ? `${p.title}: ${p.detail}` : p.title, p.correlationId);
  }

  dismiss(id: number) {
    this.items.update((l) => l.filter((t) => t.id !== id));
  }
}

@Component({
  selector: 'app-toasts',
  template: `
    <div class="toasts">
      @for (t of toasts.items(); track t.id) {
        <div class="toast" [class]="t.kind" (click)="toasts.dismiss(t.id)">
          {{ t.text }}
          @if (t.correlationId) {
            <div class="muted small">Correlation id: {{ t.correlationId }}</div>
          }
        </div>
      }
    </div>
  `,
})
export class ToastsComponent {
  readonly toasts = inject(Toasts);
}

/** Shows a problem returned by the API as a readable banner (not a generic error). */
@Component({
  selector: 'app-problem',
  template: `
    @if (problem(); as p) {
      <div class="banner error">
        <strong>{{ p.title }}</strong>
        @if (p.detail) {
          <div>{{ p.detail }}</div>
        }
        @if (p.correlationId) {
          <div class="muted small">Correlation id: {{ p.correlationId }}</div>
        }
      </div>
    }
  `,
})
export class ProblemComponent {
  readonly problem = input<Problem | null>(null);
}

/** Runs fn every ms while the component lives (used for background job progress). Create in a field initializer. */
export class Poller {
  private handle?: ReturnType<typeof setInterval>;

  constructor(destroy: DestroyRef = inject(DestroyRef)) {
    destroy.onDestroy(() => this.stop());
  }

  start(fn: () => void | Promise<void>, ms: number) {
    this.stop();
    fn();
    this.handle = setInterval(fn, ms);
  }

  stop() {
    if (this.handle) clearInterval(this.handle);
    this.handle = undefined;
  }
}

export const ballotLabel = (b: string | null | undefined) =>
  ({ Y: 'YES', N: 'NO', A: 'ABSTAIN', X: 'ABSENT' })[b ?? ''] ?? (b || '—');

export function fmtDate(s: string | null | undefined): string {
  if (!s) return '—';
  const d = new Date(s);
  return isNaN(d.getTime()) ? s : d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'medium' });
}
