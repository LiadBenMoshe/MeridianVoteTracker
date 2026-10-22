import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Session } from './session';

/** Adds the selected user and a correlation id to every API call (traceable in API logs and Azure AI calls). */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(Session);
  const correlationId = newCorrelationId();
  return next(
    req.clone({
      setHeaders: { 'X-User': session.userId(), 'X-Correlation-Id': correlationId },
    }),
  );
};

function newCorrelationId(): string {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** RFC 7807 problem returned by the API (one format for every error). */
export interface Problem {
  status: number;
  title: string;
  detail?: string;
  errors: Record<string, string[]>;
  correlationId?: string;
  body: Record<string, any>;
}

export function toProblem(e: unknown): Problem {
  if (e instanceof HttpErrorResponse) {
    const body = (e.error && typeof e.error === 'object' ? e.error : {}) as Record<string, any>;
    return {
      status: e.status,
      title: body['title'] ?? (e.status === 0 ? 'API not reachable' : e.statusText),
      detail: body['detail'] ?? (e.status === 0 ? 'Check that the API is running.' : undefined),
      errors: body['errors'] ?? {},
      correlationId: body['correlationId'] ?? e.headers?.get('X-Correlation-Id') ?? undefined,
      body,
    };
  }
  return { status: 0, title: 'Unexpected error', detail: String(e), errors: {}, body: {} };
}

/** Field error lookup, case-insensitive ("Weight" / "weight" / "Selection.VoteIds"). */
export function fieldError(p: Problem | null | undefined, field: string): string | null {
  if (!p) return null;
  const key = Object.keys(p.errors).find((k) => k.toLowerCase() === field.toLowerCase());
  return key ? p.errors[key].join(' ') : null;
}
