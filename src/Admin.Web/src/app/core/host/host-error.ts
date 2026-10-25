/** What the host said went wrong: a problem-detail body's `detail` or `title`, else the HttpErrorResponse's own `message`. */
export function hostErrorText(e: unknown, fallback = $localize`The host did not answer.`): string {
  const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
  return err?.error?.detail ?? err?.error?.title ?? err?.message ?? fallback;
}
