import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { App } from '../App';
import { server } from './setup';

const emptyList = http.get('*/messages', () => HttpResponse.json({ items: [], nextCursor: null }));

function renderApp() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

async function fillAndSend(to: string, body: string) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Recipients'), to);
  await user.type(screen.getByLabelText('Message'), body);
  await user.click(screen.getByRole('button', { name: 'Send' }));
}

test('successful send clears the form', async () => {
  let request: { to: string[]; body: string } | undefined;
  let key: string | null = null;
  server.use(
    emptyList,
    http.post('*/messages', async ({ request: req }) => {
      key = req.headers.get('Idempotency-Key');
      request = (await req.json()) as typeof request;
      return HttpResponse.json(
        { id: '1', body: 'Hi', status: 'Pending', recipients: [], createdAt: '', updatedAt: '' },
        { status: 202 },
      );
    }),
  );
  renderApp();
  await fillAndSend('0501234567, 0521112233', 'Hi');

  expect(await screen.findByText('Message accepted for sending.')).toBeInTheDocument();
  expect(screen.getByLabelText('Recipients')).toHaveValue('');
  expect(screen.getByLabelText('Message')).toHaveValue('');
  expect(request).toEqual({ to: ['0501234567', '0521112233'], body: 'Hi' });
  expect(key).toBeTruthy();
});

test('a 400 with to[1] shows the error next to recipients and keeps the fields', async () => {
  server.use(
    emptyList,
    http.post('*/messages', () =>
      HttpResponse.json(
        { title: 'Validation failed', status: 400, errors: { 'to[1]': ['Phone number must be an Israeli mobile number.'] } },
        { status: 400 },
      ),
    ),
  );
  renderApp();
  await fillAndSend('0501234567\n123', 'Hello');

  const error = await screen.findByText('123: Phone number must be an Israeli mobile number.');
  expect(error.closest('ul')).toHaveAttribute('id', 'recipients-errors');
  expect(screen.getByLabelText('Recipients')).toHaveValue('0501234567\n123');
  expect(screen.getByLabelText('Message')).toHaveValue('Hello');
});

test('the table shows Load more when nextCursor is not null', async () => {
  server.use(
    http.get('*/messages', () =>
      HttpResponse.json({
        items: [
          { id: 'a', body: 'Hello', status: 'Delivered', recipientCount: 1, deliveredCount: 1, sentCount: 0, failedCount: 0, createdAt: '2026-11-10T08:00:00Z' },
        ],
        nextCursor: 'abc',
      }),
    ),
  );
  renderApp();

  expect(await screen.findByRole('button', { name: 'Load more' })).toBeInTheDocument();
  expect(screen.getByText('Delivered', { selector: '.badge' })).toBeInTheDocument();
});
