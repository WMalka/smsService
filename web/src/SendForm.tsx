import { FormEvent, useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, sendMessage } from './api';

const MAX_BODY = 500;

export function parseRecipients(text: string): string[] {
  return text.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);
}

interface FieldErrors {
  to: string[];
  body: string[];
  other: string[];
}

function mapErrors(errors: Record<string, string[]>, to: string[]): FieldErrors {
  const result: FieldErrors = { to: [], body: [], other: [] };
  for (const [key, messages] of Object.entries(errors)) {
    const k = key.toLowerCase();
    const index = /^to\[(\d+)\]$/.exec(k)?.[1];
    if (index !== undefined) {
      const number = to[Number(index)] ?? `#${Number(index) + 1}`;
      result.to.push(...messages.map((m) => `${number}: ${m}`));
    } else if (k === 'to' || k === 'body') {
      result[k].push(...messages);
    } else {
      result.other.push(...messages);
    }
  }
  return result;
}

export function SendForm() {
  const queryClient = useQueryClient();
  const [recipients, setRecipients] = useState('');
  const [body, setBody] = useState('');
  const [success, setSuccess] = useState(false);
  // Reused only when the exact same payload is retried after a failure.
  const lastAttempt = useRef<{ payload: string; key: string } | null>(null);

  const mutation = useMutation({
    mutationFn: (v: { to: string[]; body: string; key: string }) => sendMessage(v.to, v.body, v.key),
    onSuccess: () => {
      lastAttempt.current = null;
      setRecipients('');
      setBody('');
      setSuccess(true);
      queryClient.invalidateQueries({ queryKey: ['messages'] });
    },
  });

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    const to = parseRecipients(recipients);
    const payload = JSON.stringify({ to, body });
    const attempt =
      lastAttempt.current?.payload === payload ? lastAttempt.current : { payload, key: crypto.randomUUID() };
    lastAttempt.current = attempt;
    setSuccess(false);
    mutation.mutate({ to, body, key: attempt.key });
  }

  const error = mutation.error;
  const problemErrors = error instanceof ApiError && error.status === 400 ? error.problem?.errors : undefined;
  const fieldErrors = problemErrors ? mapErrors(problemErrors, mutation.variables?.to ?? []) : null;
  const genericError = error && !fieldErrors ? 'Could not send the message. Please try again.' : null;

  return (
    <form className="card" onSubmit={onSubmit} noValidate>
      <h2>Send SMS</h2>

      <label htmlFor="recipients">Recipients</label>
      <textarea
        id="recipients"
        rows={3}
        value={recipients}
        onChange={(e) => setRecipients(e.target.value)}
        aria-describedby="recipients-hint recipients-errors"
        aria-invalid={!!fieldErrors?.to.length}
      />
      <p id="recipients-hint" className="hint">One number per line, or separated by commas.</p>
      <ul id="recipients-errors" className="field-errors">
        {fieldErrors?.to.map((m) => <li key={m}>{m}</li>)}
      </ul>

      <label htmlFor="body">Message</label>
      <textarea
        id="body"
        rows={4}
        value={body}
        onChange={(e) => setBody(e.target.value)}
        aria-describedby="body-counter body-errors"
        aria-invalid={!!fieldErrors?.body.length}
      />
      <p id="body-counter" className={`hint${body.length > MAX_BODY ? ' over' : ''}`}>
        {body.length} / {MAX_BODY}
      </p>
      <ul id="body-errors" className="field-errors">
        {fieldErrors?.body.map((m) => <li key={m}>{m}</li>)}
      </ul>

      <div role="status" aria-live="polite">
        {success && <p className="success">Message accepted for sending.</p>}
      </div>
      <div role="alert">
        {genericError && <p className="error">{genericError}</p>}
        {fieldErrors?.other.map((m) => <p key={m} className="error">{m}</p>)}
      </div>

      <button type="submit" disabled={mutation.isPending}>
        {mutation.isPending ? 'Sending…' : 'Send'}
      </button>
    </form>
  );
}
