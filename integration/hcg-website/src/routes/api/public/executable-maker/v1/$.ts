// TanStack Start server route for every Executable Maker API path under /api/public/executable-maker/v1/*.
// Mirror the file pattern of the website's existing /api/public/mobile/workspaces route if it uses another TanStack
// Start API style; the handler itself is framework-agnostic.
import { createFileRoute } from '@tanstack/react-router';
import { handleExecutableMakerApiRequest } from '@/lib/executable-maker/api.ts';
import { fromEnv } from '@/lib/executable-maker/supabase.ts';

const handle = ({ request }: { request: Request }) => {
  const { rpc, storage } = fromEnv();
  return handleExecutableMakerApiRequest(request, rpc, storage);
};

export const Route = createFileRoute('/api/public/executable-maker/v1/$')({
  server: {
    handlers: { GET: handle, POST: handle },
  },
});
