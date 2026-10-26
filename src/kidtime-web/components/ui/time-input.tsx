"use client";

import { useState, type ComponentProps } from "react";
import { Input } from "@/components/ui/input";

/**
 * A time of day typed as 24-hour "HH:MM", whatever the browser's locale.
 *
 * `<input type="time">` draws whatever clock the browser's locale uses, so a parent on an
 * English-US browser was handed AM/PM for a schedule the PC enforces on a 24-hour clock - and no
 * attribute changes that. This is a plain text field instead: digits only, the colon put in as you
 * type ("1430" reads 14:30), and a short entry finished on blur ("9" becomes 09:00). The value
 * handed upward is the text as it stands, so an unfinished time fails the editor's own check
 * rather than being guessed at.
 */
export function TimeInput({
  value,
  onValueChange,
  ...props
}: { value: string; onValueChange: (value: string) => void } & Omit<ComponentProps<typeof Input>, "value" | "onChange" | "type">) {
  const [draft, setDraft] = useState(value);
  const [shown, setShown] = useState(value);
  // A value changed from outside - a row copied to every day - replaces whatever is being typed.
  if (value !== shown) {
    setShown(value);
    setDraft(value);
  }

  function update(next: string) {
    setDraft(next);
    setShown(next);
    onValueChange(next);
  }

  return (
    <Input
      {...props}
      type="text"
      inputMode="numeric"
      autoComplete="off"
      placeholder="HH:MM"
      maxLength={5}
      value={draft}
      aria-invalid={props["aria-invalid"] || (draft !== "" && normalizeTime(draft) === null)}
      onChange={event => update(maskTime(event.target.value))}
      onBlur={event => {
        const normalized = normalizeTime(draft);
        if (normalized && normalized !== draft) update(normalized);
        props.onBlur?.(event);
      }}
    />
  );
}

/** Keeps the digits and puts the colon in after the hours. */
function maskTime(raw: string) {
  const digits = raw.replace(/\D/g, "").slice(0, 4);
  return digits.length > 2 ? `${digits.slice(0, 2)}:${digits.slice(2)}` : digits;
}

/** "9" → "09:00", "930" → "09:30", "14:30" → "14:30"; null for anything that is not a time. */
export function normalizeTime(text: string): string | null {
  const digits = text.replace(/\D/g, "");
  if (digits.length === 0 || digits.length > 4) return null;
  const [hours, minutes] = digits.length <= 2
    ? [Number(digits), 0]
    : digits.length === 3
      ? [Number(digits.slice(0, 1)), Number(digits.slice(1))]
      : [Number(digits.slice(0, 2)), Number(digits.slice(2))];
  if (hours > 23 || minutes > 59) return null;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}`;
}
