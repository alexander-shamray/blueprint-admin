/** The class the global stylesheet draws as the outline `highlight_widget` asks for. */
export const HIGHLIGHT_CLASS = 'assistant-highlight';

/** How long a widget stays outlined. */
export const HIGHLIGHT_MS = 3000;

/** How long to wait for a widget that is not drawn yet: its page may still be opening, and its frame lazy-loads. */
export const HIGHLIGHT_WAIT_MS = 3000;

const POLL_MS = 50;

const timers = new WeakMap<Element, ReturnType<typeof setTimeout>>();

function frameOf(document: Document, instanceId: string): Element | null {
  return (
    [...document.querySelectorAll('[data-widget-id]')].find((e) => e.getAttribute('data-widget-id') === instanceId) ??
    null
  );
}

/**
 * Scrolls to a widget instance's frame and outlines it for a few seconds (AI assistant design §6.4). The page a widget
 * is on may still be opening when this is asked, so it waits for the frame; false when the frame never appears.
 */
export async function highlightWidget(
  document: Document,
  instanceId: string,
  waitMs = HIGHLIGHT_WAIT_MS,
  showMs = HIGHLIGHT_MS,
): Promise<boolean> {
  const deadline = Date.now() + waitMs;
  let frame = frameOf(document, instanceId);
  while (!frame && Date.now() < deadline) {
    await new Promise<void>((resolve) => setTimeout(resolve, POLL_MS));
    frame = frameOf(document, instanceId);
  }
  if (!frame) return false;
  frame.scrollIntoView({ block: 'center', behavior: 'smooth' });
  frame.classList.add(HIGHLIGHT_CLASS);
  // One timer per frame: a repeated call restarts the outline instead of losing it to the first call's timer.
  clearTimeout(timers.get(frame));
  timers.set(
    frame,
    setTimeout(() => frame.classList.remove(HIGHLIGHT_CLASS), showMs),
  );
  return true;
}
