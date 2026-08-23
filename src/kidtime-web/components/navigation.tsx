"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { AppWindow, BarChart3, LayoutDashboard, LogOut, MonitorCog, Settings } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";

const links = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { href: "/devices", label: "Devices", icon: MonitorCog },
  { href: "/applications", label: "Applications", icon: AppWindow },
  { href: "/statistics", label: "Statistics", icon: BarChart3 },
  { href: "/settings", label: "Settings", icon: Settings },
];

export function Navigation() {
  const pathname = usePathname();
  const router = useRouter();
  async function signOut() {
    await fetch("/api/session", { method: "DELETE" });
    router.replace("/login");
    router.refresh();
  }

  return (
    <aside className="border-b bg-sidebar lg:sticky lg:top-0 lg:flex lg:h-svh lg:flex-col lg:border-r lg:border-b-0">
      <div className="flex h-14 items-center px-4 lg:h-16 lg:px-5">
        <Link className="flex items-center gap-2 font-semibold tracking-tight" href="/dashboard">
          <span className="flex size-8 items-center justify-center rounded-lg bg-primary text-sm font-bold text-primary-foreground">K</span>
          <span>KidTime</span>
        </Link>
      </div>
      <nav className="flex gap-1 overflow-x-auto px-3 pb-3 lg:flex-1 lg:flex-col lg:overflow-visible lg:pb-0" aria-label="Main navigation">
        {links.map(({ href, label, icon: Icon }) => (
          <Link
            key={href}
            href={href}
            className={cn(
              buttonVariants({ variant: "ghost", size: "lg" }),
              "shrink-0 justify-start text-muted-foreground lg:w-full",
              pathname.startsWith(href) && "bg-sidebar-accent text-sidebar-accent-foreground",
            )}
          >
            <Icon aria-hidden="true" /><span>{label}</span>
          </Link>
        ))}
      </nav>
      <div className="hidden border-t p-3 lg:block">
        <Button variant="ghost" size="lg" className="w-full justify-start text-muted-foreground" onClick={signOut}>
          <LogOut aria-hidden="true" />Sign out
        </Button>
      </div>
    </aside>
  );
}
