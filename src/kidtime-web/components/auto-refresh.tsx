"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

/**
 * Re-fetches the current server-rendered page on an interval.
 *
 * A child asking for more time is waiting on an answer with minutes left on the clock, so a parent
 * who already has the page open must not have to reload it to find out. It refreshes rather than
 * subscribing to anything: the page is already a server component, and a poll every few seconds is
 * far less machinery than a second live channel into the panel.
 */
export function AutoRefresh({ seconds = 20 }: { seconds?: number }) {
  const router = useRouter();

  useEffect(() => {
    const handle = setInterval(() => router.refresh(), seconds * 1000);
    return () => clearInterval(handle);
  }, [router, seconds]);

  return null;
}
