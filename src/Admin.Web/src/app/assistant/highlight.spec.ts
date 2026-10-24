import { HIGHLIGHT_CLASS, highlightWidget } from './highlight';

describe('highlightWidget', () => {
  afterEach(() => {
    document.body.replaceChildren();
    vi.useRealTimers();
  });

  function frame(id: string): HTMLElement {
    const el = document.createElement('div');
    el.setAttribute('data-widget-id', id);
    el.scrollIntoView = vi.fn();
    document.body.append(el);
    return el;
  }

  it('scrolls to the widget and outlines it for a while, then takes the outline off', async () => {
    vi.useFakeTimers();
    const el = frame('w1');

    expect(await highlightWidget(document, 'w1', 100, 500)).toBe(true);
    expect(el.scrollIntoView).toHaveBeenCalledTimes(1);
    expect(el.classList.contains(HIGHLIGHT_CLASS)).toBe(true);

    vi.advanceTimersByTime(500);
    expect(el.classList.contains(HIGHLIGHT_CLASS)).toBe(false);
  });

  it('waits for a frame that is drawn a little later, as a page that is still opening draws it', async () => {
    vi.useFakeTimers();
    const outcome = highlightWidget(document, 'late', 1000, 500);
    await vi.advanceTimersByTimeAsync(200);
    const el = frame('late');
    await vi.advanceTimersByTimeAsync(100);

    expect(await outcome).toBe(true);
    expect(el.classList.contains(HIGHLIGHT_CLASS)).toBe(true);
  });

  it('is false when the frame never appears, and touches no other widget', async () => {
    vi.useFakeTimers();
    const other = frame('other');
    const outcome = highlightWidget(document, 'missing', 300, 500);
    await vi.advanceTimersByTimeAsync(400);

    expect(await outcome).toBe(false);
    expect(other.classList.contains(HIGHLIGHT_CLASS)).toBe(false);
  });
});
