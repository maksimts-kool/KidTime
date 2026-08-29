"use client"

import { Slider as SliderPrimitive } from "@base-ui/react/slider"

import { cn } from "@/lib/utils"

/**
 * A single-value slider with a filled track and a round thumb, in the panel's own tokens.
 *
 * The parts are assembled here rather than exported piecemeal because every use so far is the
 * same shape: pick one number between two bounds. `ticks` draws a mark under each stop, which is
 * what makes a stepped range read as a set of choices instead of a continuous dial.
 */
function Slider({
  className,
  ticks,
  // Base UI wants the accessible name on the thumb, which is the element that takes focus and
  // reports the value, so it is taken off the root and forwarded rather than spread with the rest.
  "aria-label": label,
  ...props
}: SliderPrimitive.Root.Props<number> & { ticks?: number[] }) {
  return (
    <SliderPrimitive.Root
      data-slot="slider"
      className={cn("w-full select-none", className)}
      {...props}
    >
      <SliderPrimitive.Control className="flex h-5 w-full touch-none items-center py-1 select-none">
        <SliderPrimitive.Track className="relative h-1.5 w-full rounded-full bg-input">
          <SliderPrimitive.Indicator className="rounded-full bg-primary" />
          <SliderPrimitive.Thumb
            aria-label={label}
            className={cn(
              "size-4 rounded-full border border-primary bg-background shadow-sm transition-colors",
              "focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none",
              "data-dragging:border-primary data-dragging:bg-primary",
              "data-disabled:cursor-not-allowed data-disabled:opacity-50"
            )}
          />
        </SliderPrimitive.Track>
      </SliderPrimitive.Control>
      {ticks && (
        <div aria-hidden="true" className="mt-1 flex justify-between px-0.5">
          {ticks.map(tick => (
            <span key={tick} className="text-[10px] text-muted-foreground tabular-nums">
              {tick}
            </span>
          ))}
        </div>
      )}
    </SliderPrimitive.Root>
  )
}

export { Slider }
