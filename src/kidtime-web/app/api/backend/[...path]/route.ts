import { cookies } from "next/headers";
import { NextRequest, NextResponse } from "next/server";
import { REQUEST_ID_HEADER, describeError, elapsedMs, logger, resolveRequestId } from "@/lib/logger";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";
const log = logger("Proxy");

async function proxy(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const requestId = resolveRequestId(request.headers);
  const startedAt = performance.now();
  const { path } = await context.params;
  const route = `/api/${path.join("/")}`;
  const method = request.method;

  const token = (await cookies()).get("kidtime_session")?.value;
  if (!token) {
    log.debug(`${method} ${route} 401 (no session)`, { req: requestId });
    return withRequestId(NextResponse.json({ message: "Not signed in." }, { status: 401 }), requestId);
  }

  const target = new URL(route, backendUrl);
  target.search = request.nextUrl.search;
  const response = await fetch(target, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      [REQUEST_ID_HEADER]: requestId,
      ...(request.headers.get("content-type") ? { "Content-Type": request.headers.get("content-type")! } : {}),
      ...(request.headers.get("range") ? { Range: request.headers.get("range")! } : {}),
    },
    body: method === "GET" || method === "HEAD" ? undefined : await request.arrayBuffer(),
    cache: "no-store",
  }).catch((error: unknown) => {
    log.error(`${method} ${route} could not reach the API`, {
      req: requestId,
      ms: elapsedMs(startedAt),
      cause: describeError(error),
    });
    return null;
  });
  if (!response) {
    return withRequestId(
      NextResponse.json({ message: "KidTime API is unavailable." }, { status: 503 }),
      requestId,
    );
  }

  // The path is logged without its query: the panel puts nothing secret there, and the API's own
  // log line for the very same request - found by this id - already carries the parameters.
  const fields = { req: requestId, ms: elapsedMs(startedAt) };
  if (response.status >= 500) log.error(`${method} ${route} ${response.status}`, fields);
  else if (response.status >= 400) log.warn(`${method} ${route} ${response.status}`, fields);
  else log.debug(`${method} ${route} ${response.status}`, fields);

  if (response.status === 204) return withRequestId(new NextResponse(null, { status: 204 }), requestId);
  return withRequestId(
    new NextResponse(response.body, {
      status: response.status,
      headers: {
        "Content-Type": response.headers.get("content-type") ?? "application/json",
        ...(response.headers.get("content-disposition") ? { "Content-Disposition": response.headers.get("content-disposition")! } : {}),
        ...(response.headers.get("content-length") ? { "Content-Length": response.headers.get("content-length")! } : {}),
        ...(response.headers.get("accept-ranges") ? { "Accept-Ranges": response.headers.get("accept-ranges")! } : {}),
        ...(response.headers.get("content-range") ? { "Content-Range": response.headers.get("content-range")! } : {}),
      },
    }),
    requestId,
  );
}

/** So a parent looking at the browser's network tab has the string both containers' logs are keyed by. */
function withRequestId(response: NextResponse, requestId: string): NextResponse {
  response.headers.set(REQUEST_ID_HEADER, requestId);
  return response;
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const DELETE = proxy;
