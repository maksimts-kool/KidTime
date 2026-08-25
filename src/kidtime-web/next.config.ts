import type { NextConfig } from "next";

// Published under a path prefix on a shared reverse proxy when NEXT_PUBLIC_BASE_PATH is
// set at build time; empty (the default) keeps the panel at the domain root.
const basePath = (process.env.NEXT_PUBLIC_BASE_PATH ?? "").replace(/\/+$/, "");

const nextConfig: NextConfig = {
  output: "standalone",
  ...(basePath ? { basePath } : {}),
};

export default nextConfig;
