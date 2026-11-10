import { SendForm } from './SendForm';
import { MessagesTable } from './MessagesTable';

export function App() {
  return (
    <main>
      <h1>SMS Service</h1>
      <SendForm />
      <MessagesTable />
    </main>
  );
}
