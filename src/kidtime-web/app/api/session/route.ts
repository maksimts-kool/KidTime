import { NextRequest, NextResponse } from "next/server";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";

export async function POST(request: NextRequest) {
  const response = await fetch(`${backendUrl}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: await request.text(),
    cache: "no-store",
  }).catch(() => null);

  if (!response) return NextResponse.json({ message: "Could not reach the KidTime server." }, { status: 503 });
  const body = await response.json();
  if (!response.ok) return NextResponse.json(body, { status: response.status });

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
  return result;
}

export async function DELETE() {
  const response = NextResponse.json({ ok: true });
  response.cookies.delete("kidtime_session");
  return response;
}
