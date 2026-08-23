import type { Metadata } from "next";
import { LoginForm } from "./login-form";
import { ShieldCheck } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = { title: "Parent sign in" };

export default function LoginPage() {
  return (
    <main className="grid min-h-svh place-items-center bg-muted/40 p-4">
      <div className="w-full max-w-md">
        <div className="mb-6 flex items-center justify-center gap-2 font-semibold tracking-tight">
          <span className="flex size-9 items-center justify-center rounded-xl bg-primary text-primary-foreground">K</span>
          <span className="text-lg">KidTime</span>
        </div>
        <Card>
          <CardHeader className="text-center">
            <span className="mx-auto mb-2 flex size-10 items-center justify-center rounded-full bg-primary/10 text-primary"><ShieldCheck className="size-5" /></span>
            <CardTitle className="text-xl">Parent sign in</CardTitle>
            <CardDescription>Use the parent account configured on your private KidTime server.</CardDescription>
          </CardHeader>
          <CardContent>
          <LoginForm />
            <p className="mt-4 text-center text-xs leading-relaxed text-muted-foreground">Your password goes only to your self-hosted KidTime API and is not stored in this browser.</p>
          </CardContent>
        </Card>
        <p className="mt-4 text-center text-xs text-muted-foreground">Self-hosted · Active-time controls · No browsing history</p>
      </div>
    </main>
  );
}
