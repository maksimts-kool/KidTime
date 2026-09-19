import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";
import { REQUEST_ID_HEADER, describeError, elapsedMs, logger, resolveRequestId } from "./logger";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";
const log = logger("Backend");

export async function backendFetch<T>(path: string): Promise<T> {
  const token = (await cookies()).get("kidtime_session")?.value;
  if (!token) redirect("/login");
  const requestId = resolveRequestId(await headers());
  const startedAt = performance.now();
  const response = await fetch(`${backendUrl}${path}`, {
    headers: { Authorization: `Bearer ${token}`, [REQUEST_ID_HEADER]: requestId },
    cache: "no-store",
  }).catch((error: unknown) => {
    // The cause, not just "fetch failed". A refused connection, an unknown host and a rejected
    // certificate all reach here identically, and they are three different things to go and fix.
    log.error(`Could not reach the API for ${path}`, {
      req: requestId,
      ms: elapsedMs(startedAt),
      cause: describeError(error),
    });
    return null;
  });
  if (!response) throw new Error("KidTime API is unavailable.");
  if (response.status === 401) {
    // Ordinary: a session cookie outlives nothing in particular and a parent is simply sent back
    // to the sign-in page. Worth a line, not a warning.
    log.debug(`GET ${path} 401`, { req: requestId, ms: elapsedMs(startedAt) });
    redirect("/login");
  }
  if (!response.ok) {
    log.warn(`GET ${path} ${response.status}`, { req: requestId, ms: elapsedMs(startedAt) });
    throw new Error(`KidTime API returned ${response.status}.`);
  }
  log.debug(`GET ${path} ${response.status}`, { req: requestId, ms: elapsedMs(startedAt) });
  return response.json() as Promise<T>;
}
