import { Fragment, useState } from 'react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { getMessage, listMessages, MESSAGE_STATUSES, MessageStatus } from './api';

const REFRESH_MS = 3000;
const isActive = (s: string) => s === 'Pending' || s === 'Sent';

function StatusBadge({ status }: { status: string }) {
  const label = status === 'PartiallySent' ? 'Partially sent' : status;
  return <span className={`badge badge-${status.toLowerCase()}`}>{label}</span>;
}

function Recipients({ id }: { id: string }) {
  const query = useQuery({
    queryKey: ['messages', 'detail', id],
    queryFn: () => getMessage(id),
    refetchInterval: (q) => (q.state.data && isActive(q.state.data.status) ? REFRESH_MS : false),
  });
  if (query.isPending) return <p>Loading recipients…</p>;
  if (query.isError) return <p className="error">Could not load recipients.</p>;
  return (
    <table className="recipients">
      <thead>
        <tr><th>Number</th><th>Status</th><th>Attempts</th><th>Last error</th></tr>
      </thead>
      <tbody>
        {query.data.recipients.map((r) => (
          <tr key={r.id}>
            <td>{r.to}</td>
            <td>
              {/* Display label only; the stored status stays Pending. */}
              {r.status === 'Pending' ? <span className="badge badge-pending">Sending…</span> : <StatusBadge status={r.status} />}
            </td>
            <td>{r.attempts}</td>
            <td>{r.lastError ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function MessagesTable() {
  const [status, setStatus] = useState<MessageStatus | ''>('');
  const [expanded, setExpanded] = useState<string | null>(null);

  const query = useInfiniteQuery({
    queryKey: ['messages', 'list', status],
    queryFn: ({ pageParam }) => listMessages(status, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    refetchInterval: (q) =>
      q.state.data?.pages.some((p) => p.items.some((m) => isActive(m.status))) ? REFRESH_MS : false,
  });

  const items = query.data?.pages.flatMap((p) => p.items) ?? [];
  const toggle = (id: string) => setExpanded((cur) => (cur === id ? null : id));

  return (
    <section className="card">
      <div className="toolbar">
        <h2>Messages</h2>
        <label htmlFor="status-filter">Status</label>
        <select id="status-filter" value={status} onChange={(e) => setStatus(e.target.value as MessageStatus | '')}>
          <option value="">All</option>
          {MESSAGE_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
        <button type="button" onClick={() => query.refetch()} disabled={query.isFetching}>
          Refresh
        </button>
      </div>

      {query.isError && <p className="error" role="alert">Could not load messages.</p>}

      <div className="table-scroll">
      <table className="messages">
        <thead>
          <tr><th>Created</th><th>Message</th><th>Status</th><th>Recipients</th><th><span className="sr-only">Details</span></th></tr>
        </thead>
        <tbody>
          {items.map((m) => (
            <Fragment key={m.id}>
              <tr className="row" onClick={() => toggle(m.id)}>
                <td>{new Date(m.createdAt).toLocaleString()}</td>
                <td className="body" title={m.body}>{m.body}</td>
                <td><StatusBadge status={m.status} /></td>
                <td>{m.deliveredCount} delivered / {m.sentCount} sent / {m.failedCount} failed of {m.recipientCount}</td>
                <td>
                  <button
                    type="button"
                    aria-expanded={expanded === m.id}
                    onClick={(e) => { e.stopPropagation(); toggle(m.id); }}
                  >
                    {expanded === m.id ? 'Hide' : 'Show'}
                  </button>
                </td>
              </tr>
              {expanded === m.id && (
                <tr className="detail"><td colSpan={5}><Recipients id={m.id} /></td></tr>
              )}
            </Fragment>
          ))}
          {!query.isPending && items.length === 0 && (
            <tr><td colSpan={5}>No messages.</td></tr>
          )}
        </tbody>
      </table>
      </div>

      {query.hasNextPage && (
        <button type="button" onClick={() => query.fetchNextPage()} disabled={query.isFetchingNextPage}>
          {query.isFetchingNextPage ? 'Loading…' : 'Load more'}
        </button>
      )}
    </section>
  );
}
