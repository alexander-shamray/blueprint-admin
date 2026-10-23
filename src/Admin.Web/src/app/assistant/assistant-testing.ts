import { EventSourceLike } from './assistant-client';

/** An EventSource the spec drives: it records its URL and fires named events with ids. */
export class FakeStream implements EventSourceLike {
  readonly listeners = new Map<string, ((e: MessageEvent) => void)[]>();
  readyState = 1;
  closed = false;

  constructor(readonly url: string) {}

  addEventListener(type: string, listener: (e: MessageEvent) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }

  emit(type: string, data: unknown, id: number): void {
    for (const listener of this.listeners.get(type) ?? [])
      listener(new MessageEvent(type, { data: JSON.stringify(data), lastEventId: String(id) }));
  }

  /** What the browser does once it gives up: CLOSED, then a plain error. */
  lose(): void {
    this.readyState = 2;
    for (const listener of this.listeners.get('error') ?? []) listener(new Event('error') as MessageEvent);
  }
}

/** A host that answers each request by method and path, and keeps every request it was sent. */
export class FakeHost {
  readonly requests: { method: string; url: string; body: unknown }[] = [];
  readonly streams: FakeStream[] = [];
  private readonly routes: { method: string; url: RegExp; answer: (body: unknown) => Response }[] = [];

  on(method: string, url: RegExp, answer: (body: unknown) => Response): this {
    this.routes.unshift({ method, url, answer });
    return this;
  }

  readonly fetch = (input: string, init?: RequestInit): Promise<Response> => {
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    this.requests.push({ method, url: input, body });
    const route = this.routes.find((r) => r.method === method && r.url.test(input));
    return Promise.resolve(route ? route.answer(body) : new Response(null, { status: 404 }));
  };

  readonly openStream = (url: string): FakeStream => {
    const stream = new FakeStream(url);
    this.streams.push(stream);
    return stream;
  };

  get stream(): FakeStream {
    const open = this.streams.filter((s) => !s.closed);
    const last = open.at(-1);
    if (!last) throw new Error('No stream is open.');
    return last;
  }
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Lets every pending promise settle. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
  await new Promise((resolve) => setTimeout(resolve, 0));
}
