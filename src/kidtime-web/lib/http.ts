/** What a control says when the panel's own proxy could not be reached at all. */
export const unreachableMessage = "Could not reach the KidTime server. Try again in a moment.";

/**
 * The sentence to show for a failed API call. The API answers a request it refuses or a conflict
 * with a bare JSON string ("These dates overlap …"), which is worth showing as it is; anything
 * else - a missing record, a server fault, an empty body - gets the caller's own fallback instead.
 */
export async function errorMessage(response: Response, fallback: string): Promise<string> {
  if (response.status !== 400 && response.status !== 409) return fallback;
  const body = (await response.text().catch(() => "")).trim().replace(/^"|"$/g, "");
  return body || fallback;
}
