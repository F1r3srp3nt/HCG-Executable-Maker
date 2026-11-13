import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { createClient } from "@supabase/supabase-js";

const supabase = createClient(import.meta.env.VITE_SUPABASE_URL, import.meta.env.VITE_SUPABASE_ANON_KEY);

export async function loadOrders() {
  const res = await fetch("/api/orders");
  return res.json();
}

export function signIn() {
  return supabase.auth.signInWithOAuth({ provider: "github" });
}

createRoot(document.getElementById("root")!).render(<BrowserRouter><main>Shop</main></BrowserRouter>);
