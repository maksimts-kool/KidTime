import { appPath } from "@/lib/paths";
import Image from "next/image";
import { AppWindow } from "lucide-react";
import { cn } from "@/lib/utils";

const sizes = {
  /** Beside a legend swatch or inside a dense row, where the name carries the identity. */
  tiny: { box: "size-4 rounded-[4px]", glyph: "size-2.5", pixels: 32 },
  small: { box: "size-7 rounded-md", glyph: "size-3.5", pixels: 32 },
  default: { box: "size-9 rounded-lg", glyph: "size-4", pixels: 36 },
  large: { box: "size-16 rounded-xl", glyph: "size-7", pixels: 64 },
} as const;

export function ApplicationIcon({
  applicationId,
  displayName,
  hasIcon,
  size = "default",
}: {
  applicationId: string;
  displayName: string;
  hasIcon: boolean;
  size?: keyof typeof sizes;
}) {
  const { box, glyph, pixels } = sizes[size];
  return (
    <span className={cn("flex shrink-0 items-center justify-center overflow-hidden border bg-white shadow-xs", box)}>
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
        <AppWindow className={cn("text-muted-foreground", glyph)} aria-hidden="true" />
      )}
    </span>
  );
}
