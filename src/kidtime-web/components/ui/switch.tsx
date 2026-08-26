"use client"

import type { ReactNode } from "react"
import { Switch as SwitchPrimitive } from "@base-ui/react/switch"

import { cn } from "@/lib/utils"

type SwitchSize = "sm" | "default"

/**
 * The track and thumb only. Every control below renders this inside its own root, so the
 * clickable area is the row - the track is just what the row looks like.
 */
function SwitchTrack({ size = "default" }: { size?: SwitchSize }) {
  return (
    <span
      data-slot="switch-track"
      data-size={size}
      className="pointer-events-none relative inline-flex shrink-0 items-center rounded-full border border-transparent bg-input transition-colors group-data-[checked]/switch-control:bg-primary data-[size=default]:h-5 data-[size=default]:w-9 data-[size=sm]:h-4 data-[size=sm]:w-7 dark:bg-input/80"
    >
      <SwitchPrimitive.Thumb
        data-slot="switch-thumb"
        className="pointer-events-none block translate-x-0.5 rounded-full bg-background shadow-sm ring-0 transition-transform group-data-[size=default]/switch-control:size-4 group-data-[size=sm]/switch-control:size-3 group-data-[checked]/switch-control:translate-x-[calc(100%+2px)] dark:bg-foreground dark:group-data-[checked]/switch-control:bg-primary-foreground"
      />
    </span>
  )
}

const controlClasses =
  "group/switch-control cursor-pointer text-left transition-colors outline-none select-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 data-disabled:cursor-not-allowed data-disabled:opacity-50"

/** The bare toggle, for the rare place where the surrounding row is not ours to claim. */
function Switch({
  className,
  size = "default",
  ...props
}: SwitchPrimitive.Root.Props & { size?: SwitchSize }) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch"
      data-size={size}
      className={cn(controlClasses, "inline-flex shrink-0 items-center rounded-full", className)}
      {...props}
    >
      <SwitchTrack size={size} />
    </SwitchPrimitive.Root>
  )
}

/**
 * A whole settings row that is itself the switch: the title, the description, and the track all
 * sit inside the control, so anywhere in the row toggles it. A bare 32x18 track is a hard target
 * on a touchscreen and a fussy one with a mouse; widening the track would only make the switch
 * look clumsy, so the row grew instead.
 */
function SwitchField({
  title,
  description,
  className,
  size = "default",
  ...props
}: Omit<SwitchPrimitive.Root.Props, "children"> & {
  title: ReactNode
  description?: ReactNode
  size?: SwitchSize
}) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch-field"
      data-size={size}
      aria-label={typeof title === "string" ? title : undefined}
      className={cn(
        controlClasses,
        "flex w-full items-start gap-4 rounded-lg border border-input bg-transparent p-4",
        "hover:bg-muted/50 data-checked:border-primary/40 data-checked:bg-primary/5",
        "dark:data-checked:border-primary/25 dark:data-checked:bg-primary/10",
        className
      )}
      {...props}
    >
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-sm leading-snug font-medium">{title}</span>
        {description && (
          <span className="text-sm leading-normal font-normal text-muted-foreground">{description}</span>
        )}
      </span>
      <SwitchTrack size={size} />
    </SwitchPrimitive.Root>
  )
}

/**
 * The compact form of the same idea, for a label and a toggle that share a narrow column - the
 * weekday rows of the schedule editor. It still fills its column, so the target is the column.
 */
function SwitchOption({
  label,
  className,
  size = "default",
  ...props
}: Omit<SwitchPrimitive.Root.Props, "children"> & {
  label: ReactNode
  size?: SwitchSize
}) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch-option"
      data-size={size}
      aria-label={typeof label === "string" ? label : undefined}
      className={cn(
        controlClasses,
        "flex w-full items-center justify-between gap-3 rounded-md border border-input bg-transparent px-3 py-2 text-sm font-medium",
        "hover:bg-muted/50 data-checked:border-primary/40 data-checked:bg-primary/5",
        "dark:data-checked:border-primary/25 dark:data-checked:bg-primary/10",
        className
      )}
      {...props}
    >
      <span className="truncate">{label}</span>
      <SwitchTrack size={size} />
    </SwitchPrimitive.Root>
  )
}

export { Switch, SwitchField, SwitchOption }
