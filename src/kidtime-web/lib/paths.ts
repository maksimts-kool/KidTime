// The panel can be published under a path prefix on a shared reverse proxy. next/link
// and next/navigation prepend that prefix themselves, but fetch() and plain anchors do
// not, so every same-origin URL built by hand goes through here.
const basePath = (process.env.NEXT_PUBLIC_BASE_PATH ?? "").replace(/\/+$/, "");

export function appPath(path: string): string {
  return `${basePath}${path}`;
}
