import { Badge } from "@/components/ui/badge";

export function StatusBadge({ online }: { online: boolean }) {
  return (
    <Badge variant={online ? "secondary" : "outline"} className={online ? "text-primary" : "text-muted-foreground"}>
      <span className={online ? "size-1.5 rounded-full bg-emerald-500" : "size-1.5 rounded-full bg-zinc-400"} />
      {online ? "Online" : "Offline"}
    </Badge>
  );
}
