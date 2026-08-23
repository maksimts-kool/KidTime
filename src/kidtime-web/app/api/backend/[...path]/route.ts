import { cookies } from "next/headers";
import { NextRequest, NextResponse } from "next/server";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";

async function proxy(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const token = (await cookies()).get("kidtime_session")?.value;
  if (!token) return NextResponse.json({ message: "Not signed in." }, { status: 401 });
  const { path } = await context.params;
  const target = new URL(`/api/${path.join("/")}`, backendUrl);
  target.search = request.nextUrl.search;
  const method = request.method;
  const response = await fetch(target, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      ...(request.headers.get("content-type") ? { "Content-Type": request.headers.get("content-type")! } : {}),
    },
    body: method === "GET" || method === "HEAD" ? undefined : await request.arrayBuffer(),
    cache: "no-store",
  }).catch(() => null);
  if (!response) return NextResponse.json({ message: "KidTime API is unavailable." }, { status: 503 });
  if (response.status === 204) return new NextResponse(null, { status: 204 });
  return new NextResponse(await response.arrayBuffer(), {
    status: response.status,
    headers: { "Content-Type": response.headers.get("content-type") ?? "application/json" },
  });
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const DELETE = proxy;
