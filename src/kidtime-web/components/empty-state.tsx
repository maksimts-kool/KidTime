import { MonitorUp } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";

export function EmptyDevices() {
  return (
    <Card>
      <CardContent className="flex min-h-80 flex-col items-center justify-center px-6 text-center">
        <span className="mb-4 flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary"><MonitorUp className="size-5" /></span>
        <h2 className="font-medium">No Windows PCs are enrolled</h2>
        <p className="mt-1 max-w-md text-sm text-muted-foreground">The standalone Windows Setup workflow is not included. Existing enrolled devices continue to work normally.</p>
      </CardContent>
    </Card>
  );
}
