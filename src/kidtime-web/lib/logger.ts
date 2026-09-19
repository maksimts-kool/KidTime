/**
 * The panel's server-side log.
 *
 * The panel and the API run as two containers whose output a parent reads in one scroll, so this
 * writes the same shape the KidTime server writes - time, three-letter level, a short source, the
 * message, then the identifiers - and takes the same three settings, so one choice in `.env`
 * changes both halves of the stack. `KIDTIME_LOG_FORMAT=json` swaps to whole structured events
 * for a collector.
 *
 * It is server-only. Nothing here is imported into a client component: the browser has its own
 * console, and a logger that reached it would be one more way for a token to end up somewhere it
 * was not meant to be.
 */

type Level = "debug" | "info" | "warn" | "error";

const LEVEL_ORDER: Record<Level, number> = { debug: 10, info: 20, warn: 30, error: 40 };
const LEVEL_TAG: Record<Level, string> = { debug: "DBG", info: "INF", warn: "WRN", error: "ERR" };

/** Matches the server's column, so the two containers' lines stack up rather than interleave ragged. */
const SOURCE_WIDTH = 22;

const minimum = LEVEL_ORDER[normalizeLevel(process.env.KIDTIME_LOG_LEVEL)];
const json = (process.env.KIDTIME_LOG_FORMAT ?? "pretty").toLowerCase() === "json";
const colour = resolveColour();

/**
 * The four levels, plus the .NET spellings of them. One `KIDTIME_LOG_LEVEL` in `.env` is meant to
 * set both containers at once, and the server reads Microsoft's names - so "Warning" has to mean
 * `warn` here rather than quietly falling back to `info` and making the panel the chatty one.
 */
function normalizeLevel(value: string | undefined): Level {
  const candidate = (value ?? "info").toLowerCase();
  if (candidate in LEVEL_ORDER) return candidate as Level;
  switch (candidate) {
    case "trace":
      return "debug";
    case "information":
      return "info";
    case "warning":
      return "warn";
    case "critical":
      return "error";
    default:
      return "info";
  }
}

/**
 * `auto` gives up inside a container, where stdout is a pipe and `docker compose logs` would
 * render the colour perfectly well - hence `always`, which is what compose passes.
 */
function resolveColour(): boolean {
  const choice = (process.env.KIDTIME_LOG_COLOR ?? "auto").toLowerCase();
  if (choice === "never") return false;
  if (choice === "always") return true;
  if (process.env.NO_COLOR) return false;
  return process.stdout.isTTY === true;
}

const ANSI = {
  reset: "[0m",
  dim: "[38;5;244m",
  time: "[38;5;245m",
  source: "[38;5;110m",
  message: "[38;5;252m",
  debug: "[38;5;245m",
  info: "[38;5;77m",
  warn: "[38;5;214m",
  error: "[38;5;203m",
} as const;

function paint(code: string, text: string): string {
  return colour ? `${code}${text}${ANSI.reset}` : text;
}

/** Values attached to one line: a correlation id, a status, how long something took. */
export type LogFields = Record<string, string | number | boolean | null | undefined>;

/**
 * What actually went wrong, as one short string.
 *
 * `fetch` rejects with a bare "fetch failed" and puts the reason - a refused connection, an
 * unknown host, a certificate the panel will not accept - in `cause`. The panel used to discard
 * the whole thing and tell the parent "KidTime API is unavailable", which is true of all four and
 * useful for none of them.
 */
export function describeError(error: unknown): string {
  if (!(error instanceof Error)) return String(error);
  const cause = error.cause;
  if (cause instanceof Error && cause.message && cause.message !== error.message) {
    const code = "code" in cause && typeof cause.code === "string" ? ` (${cause.code})` : "";
    return `${error.name}: ${error.message} - ${cause.message}${code}`;
  }
  return `${error.name}: ${error.message}`;
}

function write(level: Level, source: string, message: string, fields?: LogFields): void {
  if (LEVEL_ORDER[level] < minimum) return;
  const entries = Object.entries(fields ?? {}).filter(([, value]) => value !== undefined && value !== null);

  if (json) {
    process.stdout.write(
      `${JSON.stringify({
        Timestamp: new Date().toISOString(),
        LogLevel: level,
        Category: source,
        Message: message,
        State: Object.fromEntries(entries),
      })}\n`,
    );
    return;
  }

  const time = new Date().toTimeString().slice(0, 8);
  const trailer = entries.length > 0 ? paint(ANSI.dim, `  ${entries.map(([k, v]) => `${k}=${v}`).join(" ")}`) : "";
  process.stdout.write(
    `${paint(ANSI.time, time)} ${paint(ANSI[level], LEVEL_TAG[level])} ` +
      `${paint(ANSI.source, source.padEnd(SOURCE_WIDTH).slice(0, SOURCE_WIDTH))} ` +
      `${paint(ANSI.message, message)}${trailer}\n`,
  );
}

/**
 * A logger for one source. The name is what fills the third column, so it names the boundary the
 * line came from - "Proxy", "Session", "Backend" - rather than a file.
 */
export function logger(source: string) {
  return {
    debug: (message: string, fields?: LogFields) => write("debug", source, message, fields),
    info: (message: string, fields?: LogFields) => write("info", source, message, fields),
    warn: (message: string, fields?: LogFields) => write("warn", source, message, fields),
    error: (message: string, fields?: LogFields) => write("error", source, message, fields),
  };
}

/**
 * The header both containers join their lines on. The panel mints the id when the browser did not
 * bring one, sends it to the API, and logs it either way, so one click in the panel reads as one
 * id across the web log, the server log, and the browser's own network tab.
 */
export const REQUEST_ID_HEADER = "X-Request-Id";

export function resolveRequestId(headers: Headers): string {
  const supplied = headers.get(REQUEST_ID_HEADER);
  if (supplied) {
    const cleaned = supplied.replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
    if (cleaned) return cleaned;
  }
  return crypto.randomUUID().replaceAll("-", "").slice(0, 8);
}

/** Milliseconds since a `performance.now()` mark, to one decimal - the same precision the server logs. */
export function elapsedMs(startedAt: number): number {
  return Math.round((performance.now() - startedAt) * 10) / 10;
}
