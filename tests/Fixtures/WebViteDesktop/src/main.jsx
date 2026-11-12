import { StrictMode, Suspense, lazy } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Route, Routes, useParams } from 'react-router-dom';
import './styles.css';

// Code-split: the archive page is its own chunk, loaded only when the route is opened.
const Archive = lazy(() => import('./Archive.jsx'));

const notes = [
  { id: 'harbour', title: 'Harbour survey', body: 'Tide tables and mooring positions.' },
  { id: 'ridge', title: 'Ridge walk', body: 'Waypoints along the northern ridge.' },
];

function Home() {
  return (
    <main>
      <h1>Field Notes</h1>
      <p data-testid="home">All notes are bundled with the app.</p>
      <ul>{notes.map((n) => <li key={n.id}><Link to={`/notes/${n.id}`}>{n.title}</Link></li>)}</ul>
      <p><Link to="/archive/2024" data-testid="archive-link">Archive</Link></p>
      <p><a href="https://example.com/field-guide" target="_blank" rel="noreferrer" data-testid="external">Field guide (opens in your browser)</a></p>
      <p><a href="file:///etc/hosts" data-testid="file-link">Local file (refused)</a></p>
      <img src="/favicon.svg" alt="" width="24" height="24" />
    </main>
  );
}

function Note() {
  const { id } = useParams();
  const note = notes.find((n) => n.id === id);
  return (
    <main>
      <h1 data-testid="note">{note ? note.title : 'Note not found'}</h1>
      <p>{note?.body}</p>
      <Link to="/">All notes</Link>
    </main>
  );
}

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BrowserRouter>
      <Suspense fallback={<p>Loading…</p>}>
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/notes/:id" element={<Note />} />
          <Route path="/archive/:year" element={<Archive />} />
        </Routes>
      </Suspense>
    </BrowserRouter>
  </StrictMode>,
);
