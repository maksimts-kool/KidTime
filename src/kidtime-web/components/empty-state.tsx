import Link from "next/link";
import { MonitorUp } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

export function EmptyDevices() {
  return (
    <Card>
      <CardContent className="flex min-h-80 flex-col items-center justify-center px-6 text-center">
        <span className="mb-4 flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary"><MonitorUp className="size-5" /></span>
        <h2 className="font-medium">Enroll your first Windows PC</h2>
        <p className="mt-1 mb-5 max-w-md text-sm text-muted-foreground">Create a one-time enrollment token, then deploy the ControlService to the controlled computer.</p>
        <Button render={<Link href="/settings" />}>Create enrollment token</Button>
      </CardContent>
    </Card>
  );
}
