import { backendFetch } from "@/lib/backend";
import { Navigation } from "@/components/navigation";
import { Badge } from "@/components/ui/badge";

export default async function PanelLayout({ children }: { children: React.ReactNode }) {
  const user = await backendFetch<{ email: string }>("/api/auth/me");
  return (
    <div className="min-h-svh bg-muted/30 lg:grid lg:grid-cols-[240px_minmax(0,1fr)]">
      <Navigation />
      <div className="min-w-0">
        <header className="sticky top-0 z-20 flex h-14 items-center justify-between border-b bg-background/90 px-4 backdrop-blur sm:px-6 lg:px-8">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <span className="size-1.5 rounded-full bg-emerald-500" />Private home server
          </div>
          <Badge variant="outline" className="max-w-56 truncate font-normal">{user.email}</Badge>
        </header>
        <main className="mx-auto w-full max-w-7xl p-4 sm:p-6 lg:p-8">{children}</main>
      </div>
    </div>
  );
}
