import type { LucideIcon } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function StatCard({
  label,
  value,
  detail,
  icon: Icon,
}: {
  label: string;
  value: string;
  detail?: string;
  icon?: LucideIcon;
}) {
  return (
    <Card size="sm">
      <CardHeader className="flex-row items-center justify-between">
        <CardDescription>{label}</CardDescription>
        {Icon && <Icon className="size-4 text-muted-foreground" aria-hidden="true" />}
      </CardHeader>
      <CardContent>
        <CardTitle className="text-xl tabular-nums">{value}</CardTitle>
        {detail && <p className="mt-1 text-xs text-muted-foreground">{detail}</p>}
      </CardContent>
    </Card>
  );
}
