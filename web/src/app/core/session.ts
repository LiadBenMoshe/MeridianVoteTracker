import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from '../api/api';
import { apiUsersGet } from '../api/functions';
import { UserDto } from '../api/models';

/**
 * Simplified identity: the selected user is sent as the X-User header.
 * The API enforces roles; the UI only uses the role to hide controls that would be refused anyway.
 */
@Injectable({ providedIn: 'root' })
export class Session {
  private readonly api = inject(Api);
  readonly users = signal<UserDto[]>([]);
  readonly userId = signal<string>(read() ?? 'dana');
  readonly user = computed(() => this.users().find((u) => u.id === this.userId()));
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  async load() {
    this.users.set(await this.api.invoke(apiUsersGet));
  }

  switchTo(id: string) {
    this.userId.set(id);
    try {
      localStorage.setItem('meridian.user', id);
    } catch {
      /* storage unavailable: keep in memory */
    }
  }
}

function read(): string | null {
  try {
    return localStorage.getItem('meridian.user');
  } catch {
    return null;
  }
}
