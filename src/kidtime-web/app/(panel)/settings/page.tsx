import { CheckCircle2, CircleSlash2 } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export default function SettingsPage() {
  return (
    <>
      <PageHeader eyebrow="Administration" title="Settings" description="Privacy information for this installation. Add Windows PCs from the Devices page." />
      <div className="grid gap-4">
        <Card>
          <CardHeader><CardTitle>Privacy scope</CardTitle><CardDescription>KidTime collects only what is required for local screen-time enforcement.</CardDescription></CardHeader>
          <CardContent className="grid gap-4 md:grid-cols-2">
            <div className="rounded-lg border bg-muted/40 p-4"><CheckCircle2 className="mb-3 size-5 text-primary" /><h3 className="text-sm font-medium">Included</h3><p className="mt-1 text-sm leading-relaxed text-muted-foreground">Active PC time, foreground application identity, idle state, device health, and local enforcement events.</p></div>
            <div className="rounded-lg border bg-muted/40 p-4"><CircleSlash2 className="mb-3 size-5 text-muted-foreground" /><h3 className="text-sm font-medium">Never collected</h3><p className="mt-1 text-sm leading-relaxed text-muted-foreground">Websites, searches, keystrokes, screenshots, messages, camera, microphone, or network traffic.</p></div>
          </CardContent>
        </Card>
      </div>
    </>
  );
}
