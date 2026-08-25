import { appPath } from "@/lib/paths";
import Image from "next/image";
import { AppWindow } from "lucide-react";
import { cn } from "@/lib/utils";

export function ApplicationIcon({
  applicationId,
  displayName,
  hasIcon,
  size = "default",
}: {
  applicationId: string;
  displayName: string;
  hasIcon: boolean;
  size?: "default" | "large";
}) {
  const pixels = size === "large" ? 64 : 36;
  return (
    <span
      className={cn(
        "flex shrink-0 items-center justify-center overflow-hidden rounded-lg border bg-white shadow-xs",
        size === "large" ? "size-16 rounded-xl" : "size-9",
      )}
    >
      {hasIcon ? (
        <Image
          src={appPath(`/api/backend/applications/${applicationId}/icon`)}
          width={pixels}
          height={pixels}
          sizes={`${pixels}px`}
          alt={`${displayName} icon`}
          className="size-full object-contain"
          unoptimized
        />
      ) : (
        <AppWindow className={cn("text-muted-foreground", size === "large" ? "size-7" : "size-4")} aria-hidden="true" />
      )}
    </span>
  );
}
