import { NextRequest, NextResponse } from "next/server";
import { REQUEST_ID_HEADER, describeError, elapsedMs, logger, resolveRequestId } from "@/lib/logger";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";
const log = logger("Session");

export async function POST(request: NextRequest) {
  const requestId = resolveRequestId(request.headers);
  const startedAt = performance.now();
  const response = await fetch(`${backendUrl}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json", [REQUEST_ID_HEADER]: requestId },
    body: await request.text(),
    cache: "no-store",
  }).catch((error: unknown) => {
    log.error("Could not reach the API to sign in", {
      req: requestId,
      ms: elapsedMs(startedAt),
      cause: describeError(error),
    });
    return null;
  });

  if (!response) {
    return NextResponse.json({ message: "Could not reach the KidTime server." }, { status: 503 });
  }
  const body = await response.json();
  if (!response.ok) {
    // A refused sign-in on a self-hosted panel is the one thing worth seeing in a log without
    // being asked: it is either a parent who mistyped or somebody who should not be trying. The
    // address goes nowhere near the line - neither does the password, which is why only the
    // status is here.
    log.warn(`Sign-in refused (${response.status})`, { req: requestId, ms: elapsedMs(startedAt) });
    return NextResponse.json(body, { status: response.status });
  }

  const result = NextResponse.json({ user: body.user });
  const forwardedProtocol = request.headers.get("x-forwarded-proto")?.split(",")[0]?.trim();
  const isHttps = forwardedProtocol ? forwardedProtocol === "https" : request.nextUrl.protocol === "https:";
  result.cookies.set("kidtime_session", body.token, {
    httpOnly: true,
    sameSite: "strict",
    secure: isHttps,
    path: "/",
    maxAge: 12 * 60 * 60,
  });
  log.info("Signed in", { req: requestId, ms: elapsedMs(startedAt), secure: isHttps });
  return result;
}

export async function DELETE() {
  const response = NextResponse.json({ ok: true });
  response.cookies.delete("kidtime_session");
  log.info("Signed out");
  return response;
}
