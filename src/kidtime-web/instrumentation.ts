/**
 * What the panel says about itself when it starts, and what it says when a request fails inside
 * React rather than at one of the API boundaries.
 *
 * `onRequestError` is Next.js' one hook for the errors that escape a server component or a route
 * handler. Without it those reach the container log as a bare stack trace with no route attached,
 * which on a two-container stack is the hardest kind of line to place.
 */

import type { Instrumentation } from "next";

export async function register() {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { logger } = await import("./lib/logger");
  const log = logger("Startup");
  log.info(
    `KidTime panel · API ${process.env.KIDTIME_API_URL ?? "https://localhost:5081"} · ` +
      `base path ${process.env.NEXT_PUBLIC_BASE_PATH || "/"}`,
  );
}

export const onRequestError: Instrumentation.onRequestError = async (error, request, context) => {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { describeError, logger, REQUEST_ID_HEADER } = await import("./lib/logger");
  const requestId = request.headers?.[REQUEST_ID_HEADER.toLowerCase()];
  logger("Render").error(`${request.method} ${request.path} failed in ${context.routeType}`, {
    req: typeof requestId === "string" ? requestId : undefined,
    route: context.routePath,
    // React replaces an error thrown in a server component with one carrying a digest, and the
    // digest is the only thing the browser is shown. Logging it is what lets a parent reading a
    // blank page quote a number that finds this line.
    digest: typeof error === "object" && error !== null && "digest" in error ? String(error.digest) : undefined,
    cause: describeError(error),
  });
};
