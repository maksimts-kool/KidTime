import Link from "next/link";
import { Clock } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { AccountMenu } from "@/components/account-menu";
import { Navigation } from "@/components/navigation";
import { Button } from "@/components/ui/button";

export default async function PanelLayout({ children }: { children: React.ReactNode }) {
  const [user, pending] = await Promise.all([
    backendFetch<{ email: string }>("/api/auth/me"),
    backendFetch<{ count: number }>("/api/time-extensions/pending/count"),
  ]);
  return (
    <div className="min-h-svh bg-muted/30 lg:grid lg:grid-cols-[240px_minmax(0,1fr)]">
      <Navigation />
      <div className="min-w-0">
        <header className="sticky top-0 z-20 flex h-14 items-center justify-between gap-3 border-b bg-background/90 px-4 backdrop-blur sm:px-6 lg:px-8">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <span className="size-1.5 rounded-full bg-emerald-500" />Private home server
          </div>
          <div className="flex items-center gap-1">
            {/* A child waiting on an answer is the one thing worth seeing from every page. */}
            {pending.count > 0 && (
              <Button variant="ghost" size="lg" nativeButton={false} render={<Link href="/requests" />} className="text-primary">
                <Clock data-icon="inline-start" />
                <span>{pending.count} waiting</span>
              </Button>
            )}
            <AccountMenu email={user.email} />
          </div>
        </header>
        <main className="mx-auto w-full max-w-7xl p-4 pb-24 sm:p-6 sm:pb-24 lg:p-8 lg:pb-24">{children}</main>
      </div>
    </div>
  );
}
