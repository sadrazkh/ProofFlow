import { t } from './i18n';

/**
 * The one place a fetch is written.
 *
 * It exists so three things cannot be forgotten: the antiforgery header on every mutation, a
 * message a person can read when the server refuses, and a distinction between "the API said no"
 * and "the network is gone". A component that writes its own fetch will forget at least one.
 */

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

function csrf(): string {
  return document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value
    ?? document.querySelector<HTMLMetaElement>('meta[name="csrf-token"]')?.content
    ?? '';
}

async function request<T>(method: string, url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  let response: Response;

  try {
    response = await fetch(url, {
      method,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(method === 'GET' ? {} : { 'X-CSRF-Token': csrf() }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch (error) {
    if ((error as Error).name === 'AbortError') throw error;
    // A failed fetch is not a failed request — it may never have left the machine. Saying so
    // matters, because "try again" is safe here and is not always safe after a 500.
    throw new ApiError(t('error.body'), 0);
  }

  if (response.status === 204) return undefined as T;

  const isJson = response.headers.get('content-type')?.includes('application/json') ?? false;
  const payload = isJson ? await response.json().catch(() => null) : null;

  if (!response.ok) {
    throw new ApiError(
      (payload?.detail as string) ?? (payload?.title as string) ?? t('error.body'),
      response.status,
      (payload?.errors as Record<string, string[]>) ?? {},
    );
  }

  return payload as T;
}

export const api = {
  get: <T>(url: string, signal?: AbortSignal) => request<T>('GET', url, undefined, signal),
  post: <T>(url: string, body?: unknown, signal?: AbortSignal) => request<T>('POST', url, body, signal),
  put: <T>(url: string, body?: unknown, signal?: AbortSignal) => request<T>('PUT', url, body, signal),
  patch: <T>(url: string, body?: unknown, signal?: AbortSignal) => request<T>('PATCH', url, body, signal),
  delete: <T>(url: string, signal?: AbortSignal) => request<T>('DELETE', url, undefined, signal),
};
