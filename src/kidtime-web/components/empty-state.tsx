import { MonitorUp } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { AddDevice } from "@/components/add-device";

export function EmptyDevices() {
  return (
    <Card>
      <CardContent className="flex min-h-80 flex-col items-center justify-center px-6 text-center">
        <span className="mb-4 flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary"><MonitorUp className="size-5" /></span>
        <h2 className="font-medium">Enroll your first Windows PC</h2>
        <p className="mt-1 mb-5 max-w-md text-sm text-muted-foreground">Download the Windows setup, choose the child account, and connect it in a few guided steps.</p>
        <AddDevice emptyState />
      </CardContent>
    </Card>
  );
}
