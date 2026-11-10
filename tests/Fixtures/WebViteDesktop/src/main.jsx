import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Route, Routes, useParams } from 'react-router-dom';
import './styles.css';

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
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/notes/:id" element={<Note />} />
      </Routes>
    </BrowserRouter>
  </StrictMode>,
);
