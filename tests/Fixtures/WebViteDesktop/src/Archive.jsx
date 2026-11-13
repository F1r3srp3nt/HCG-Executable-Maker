import { Link, useParams } from 'react-router-dom';
import compass from './compass.svg';

// A separate chunk (React.lazy) with its own asset: both must load from inside the packaged app.
export default function Archive() {
  const { year } = useParams();
  return (
    <main>
      <h1 data-testid="archive">Archive {year}</h1>
      <img src={compass} alt="compass" width="32" height="32" data-testid="archive-image" />
      <Link to="/">All notes</Link>
    </main>
  );
}
