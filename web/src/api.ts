export const MESSAGE_STATUSES = ['Pending', 'Sent', 'Delivered', 'PartiallySent', 'Failed'] as const;
export type MessageStatus = (typeof MESSAGE_STATUSES)[number];
export type RecipientStatus = 'Pending' | 'Sent' | 'Delivered' | 'Failed';

export interface RecipientDto {
  id: string;
  to: string;
  status: RecipientStatus;
  attempts: number;
  lastError: string | null;
}

export interface MessageDto {
  id: string;
  body: string;
  status: MessageStatus;
  recipients: RecipientDto[];
  createdAt: string;
  updatedAt: string;
}

export interface MessageListItem {
  id: string;
  body: string;
  status: MessageStatus;
  recipientCount: number;
  deliveredCount: number;
  sentCount: number;
  failedCount: number;
  createdAt: string;
}

export interface MessagePage {
  items: MessageListItem[];
  nextCursor: string | null;
}

export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  constructor(public status: number, public problem: ProblemDetails | null) {
    super(problem?.title ?? `Request failed (${status})`);
  }
}

// Absolute URL: works in the browser (same origin / Vite proxy) and in jsdom tests, where Node fetch rejects relative URLs.
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(new URL(path, window.location.origin), init);
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new ApiError(res.status, data);
  return data as T;
}

export function sendMessage(to: string[], body: string, idempotencyKey: string) {
  return request<MessageDto>('/messages', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': idempotencyKey },
    body: JSON.stringify({ to, body }),
  });
}

export function getMessage(id: string) {
  return request<MessageDto>(`/messages/${encodeURIComponent(id)}`);
}

export function listMessages(status: MessageStatus | '', cursor: string | null, limit = 20) {
  const q = new URLSearchParams({ limit: String(limit) });
  if (status) q.set('status', status);
  if (cursor) q.set('cursor', cursor);
  return request<MessagePage>(`/messages?${q}`);
}
