"use client";

import { useRouter } from "next/navigation";
import { ChevronDown, LogOut } from "lucide-react";
import { appPath } from "@/lib/paths";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

/**
 * Who is signed in, and the way out. It sits in the header on every screen size, so a parent on a
 * phone - where the sidebar's footer is not drawn - can still sign out.
 */
export function AccountMenu({ email }: { email: string }) {
  const router = useRouter();
  const initial = email.trim().charAt(0).toUpperCase() || "?";

  async function signOut() {
    await fetch(appPath("/api/session"), { method: "DELETE" }).catch(() => null);
    router.replace("/login");
    router.refresh();
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        render={<Button variant="ghost" size="lg" className="gap-2 pr-2 pl-1" aria-label={`Account: ${email}`} />}
      >
        <Avatar size="sm">
          <AvatarFallback className="bg-primary text-xs font-semibold text-primary-foreground">{initial}</AvatarFallback>
        </Avatar>
        <span className="hidden max-w-56 truncate text-sm font-normal sm:inline">{email}</span>
        <ChevronDown className="text-muted-foreground" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuGroup>
          <DropdownMenuLabel>
            <span className="block text-xs font-normal text-muted-foreground">Signed in as</span>
            <span className="block truncate text-sm font-medium text-foreground">{email}</span>
          </DropdownMenuLabel>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={signOut}>
          <LogOut />Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
