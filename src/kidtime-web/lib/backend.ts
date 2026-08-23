import { cookies } from "next/headers";
import { redirect } from "next/navigation";

const backendUrl = process.env.KIDTIME_API_URL ?? "https://localhost:5081";

export async function backendFetch<T>(path: string): Promise<T> {
  const token = (await cookies()).get("kidtime_session")?.value;
  if (!token) redirect("/login");
  const response = await fetch(`${backendUrl}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: "no-store",
  }).catch(() => null);
  if (!response) throw new Error("KidTime API is unavailable.");
  if (response.status === 401) redirect("/login");
  if (!response.ok) throw new Error(`KidTime API returned ${response.status}.`);
  return response.json() as Promise<T>;
}
