import { ClientToolRegistry } from './client-tools';
import {
  AssistantEvent,
  AssistantEvents,
  AssistantStatus,
  ClientToolResult,
  ConversationItem,
  ConversationOpened,
  ConversationView,
  EVENT_NAMES,
  OpenConversation,
  StartTurn,
} from './protocol';

/** The part of EventSource the client uses, so a test can stand in for it. */
export interface EventSourceLike {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
}

/** A problem the host answered, its status and RFC 9457 title kept for the caller to word. */
export class AssistantProblem extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail: string | null,
  ) {
    super(detail ?? title);
  }
}

/**
 * `/api/assistant` over fetch and EventSource (AI assistant design §5.1, §5.3). Framework-free, and shared in shape with
 * the platform's web client (§13): it knows the protocol and nothing of the console.
 */
export class AssistantClient {
  constructor(
    private readonly base = '/api/assistant',
    private readonly http: (input: string, init?: RequestInit) => Promise<Response> = (input, init) =>
      fetch(input, init),
    private readonly openStream: (url: string) => EventSourceLike = (url) => new EventSource(url),
  ) {}

  /** The status, or null when the host could not say (a host without the route, a network failure). */
  async status(): Promise<AssistantStatus | null> {
    try {
      const response = await this.http(this.base);
      return response.ok ? ((await response.json()) as AssistantStatus) : null;
    } catch {
      return null;
    }
  }

  async open(request: OpenConversation): Promise<string> {
    const response = await this.post('/conversations', request);
    return ((await response.json()) as ConversationOpened).id;
  }

  /** The conversation as the client shows it, or null when the host no longer holds it. */
  async read(id: string): Promise<ConversationView | null> {
    const response = await this.http(this.conversation(id));
    if (response.status === 404) return null;
    if (!response.ok) throw await problem(response);
    return (await response.json()) as ConversationView;
  }

  /** Starts a turn: `running` when one already runs (409), `gone` when the host no longer holds the conversation. */
  async send(id: string, turn: StartTurn): Promise<'started' | 'running' | 'gone'> {
    const response = await this.http(`${this.conversation(id)}/turns`, json('POST', turn));
    if (response.status === 409) return 'running';
    if (response.status === 404) return 'gone';
    if (!response.ok) throw await problem(response);
    return 'started';
  }

  async answer(id: string, result: ClientToolResult): Promise<void> {
    // A 404 is a call the host already gave up on (its timeout, a cancel); there is nothing left to tell it.
    const response = await this.http(`${this.conversation(id)}/tool-results`, json('POST', result));
    if (!response.ok && response.status !== 404) throw await problem(response);
  }

  async cancel(id: string): Promise<void> {
    await this.http(`${this.conversation(id)}/cancel`, { method: 'POST' });
  }

  async forget(id: string): Promise<void> {
    await this.http(this.conversation(id), { method: 'DELETE' });
  }

  /**
   * Follows the conversation's events after `afterId`. EventSource reconnects on its own with Last-Event-ID; `onLost`
   * runs only once it has given up (the host answered 404 for a conversation it no longer holds). Returns the close.
   */
  follow(id: string, afterId: number, onEvent: (event: AssistantEvent) => void, onLost: () => void): () => void {
    const source = this.openStream(`${this.conversation(id)}/events?after=${afterId}`);
    for (const name of EVENT_NAMES) {
      source.addEventListener(name, (e: MessageEvent) => {
        // The protocol's `error` shares its name with EventSource's own, which a dropped connection fires with no
        // data; only an event the host sent carries any.
        if (typeof e.data !== 'string') return;
        onEvent({ name, data: JSON.parse(e.data), id: Number(e.lastEventId) } as AssistantEvent);
      });
    }
    source.addEventListener('error', () => {
      if (source.readyState === 2) onLost();
    });
    return () => source.close();
  }

  private conversation(id: string): string {
    return `${this.base}/conversations/${encodeURIComponent(id)}`;
  }

  private async post(path: string, body: unknown): Promise<Response> {
    const response = await this.http(`${this.base}${path}`, json('POST', body));
    if (!response.ok) throw await problem(response);
    return response;
  }
}

function json(method: string, body: unknown): RequestInit {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

async function problem(response: Response): Promise<AssistantProblem> {
  try {
    const body = (await response.json()) as { title?: string; detail?: string };
    return new AssistantProblem(response.status, body.title ?? response.statusText, body.detail ?? null);
  } catch {
    return new AssistantProblem(response.status, response.statusText, null);
  }
}

/** What a client shows of its one conversation. */
export interface ConversationState {
  id: string | null;
  items: ConversationItem[];
  running: boolean;
  lastEventId: number;
  /** Why the last action failed, as a code the client words, with the host's detail when it gave one. */
  problem: { code: 'turn-running' | 'failed' | 'provider' | 'gone'; detail: string | null } | null;
}

export const EMPTY_CONVERSATION: ConversationState = {
  id: null,
  items: [],
  running: false,
  lastEventId: 0,
  problem: null,
};

/** Where a client keeps its conversation's id between reloads of one tab (§5.5). */
export interface IdStore {
  get(): string | null;
  set(id: string | null): void;
}

/**
 * One conversation, as the client sees it (AI assistant design §2, §5.1): the items it shows, kept current from the
 * event stream, and the client tools a turn calls, run through the registry and answered. The stream is open while a
 * turn runs, so client calls are answered with nothing visible, and while something showing the conversation is
 * visible; an idle, hidden conversation holds no stream and no timer. After a reload it redraws from the conversation
 * read and follows on from that read's `lastEventId`. Framework-free; `onChange` hands each new state to the view.
 */
export class AssistantConversation {
  private state: ConversationState = EMPTY_CONVERSATION;
  private visible = false;
  private close: (() => void) | null = null;
  private readonly answered = new Set<string>();
  /** Counts messages sent, so a read begun before the latest one never overwrites what it started. */
  private sends = 0;

  constructor(
    private readonly client: AssistantClient,
    private readonly tools: ClientToolRegistry,
    private readonly opening: () => OpenConversation,
    private readonly ids: IdStore,
    private readonly onChange: (state: ConversationState) => void,
  ) {}

  get current(): ConversationState {
    return this.state;
  }

  /** Redraws the tab's conversation, if the host still holds it, and follows it when a turn is still running. */
  async resume(): Promise<void> {
    const id = this.ids.get();
    if (!id) return;
    const view = await this.client.read(id).catch(() => null);
    if (!view) {
      this.ids.set(null);
      return;
    }
    this.update({ id, items: view.items, running: view.running, lastEventId: view.lastEventId, problem: null });
    this.settleStream();
    // The stream follows on after the read, so a call emitted before the reload is answered from the read.
    for (const call of view.pendingCalls) this.run(call);
  }

  setVisible(visible: boolean): void {
    this.visible = visible;
    this.settleStream();
  }

  /** `hostOptions` ride with the turn (§5.1): the host's tools read them, here which realm user the platform reads are made as. */
  async send(
    text: string,
    context: Record<string, unknown> | null,
    locale: string,
    hostOptions: Record<string, unknown> | null = null,
  ): Promise<void> {
    if (this.state.running || text.trim() === '') return;
    this.sends++;
    const items: ConversationItem[] = [...this.state.items, { kind: 'operator', text }];
    this.update({ ...this.state, items, running: true, problem: null });
    try {
      let id = this.state.id ?? (await this.openNew());
      this.settleStream();
      let outcome = await this.client.send(id, { text, context, locale, hostOptions });
      if (outcome === 'gone') {
        // The host was restarted, or forgot the conversation: the same words go to a new one.
        this.stopStream();
        id = await this.openNew();
        this.settleStream();
        outcome = await this.client.send(id, { text, context, locale, hostOptions });
      }
      if (outcome === 'running') {
        this.update({
          ...this.state,
          items: this.state.items.slice(0, -1),
          problem: { code: 'turn-running', detail: null },
        });
      }
    } catch (e) {
      this.update({
        ...this.state,
        running: false,
        problem: { code: 'failed', detail: e instanceof Error ? e.message : String(e) },
      });
      this.settleStream();
    }
  }

  async cancel(): Promise<void> {
    if (this.state.id && this.state.running) await this.client.cancel(this.state.id);
  }

  /** Forgets the conversation here and on the host; the next message opens a new one. */
  async startOver(): Promise<void> {
    const id = this.state.id;
    this.stopStream();
    this.ids.set(null);
    this.answered.clear();
    this.update(EMPTY_CONVERSATION);
    if (id) await this.client.forget(id).catch(() => undefined);
  }

  private async openNew(): Promise<string> {
    const id = await this.client.open(this.opening());
    this.ids.set(id);
    this.update({ ...this.state, id, lastEventId: 0 });
    return id;
  }

  private settleStream(): void {
    const wanted = this.state.id !== null && (this.state.running || this.visible);
    if (wanted && !this.close && this.state.id) {
      this.close = this.client.follow(
        this.state.id,
        this.state.lastEventId,
        (e) => this.receive(e),
        () => void this.lost(),
      );
    } else if (!wanted) {
      this.stopStream();
    }
  }

  private stopStream(): void {
    this.close?.();
    this.close = null;
  }

  private receive(event: AssistantEvent): void {
    // A stream reopened after a read replays what that read already holds.
    if (event.id <= this.state.lastEventId) return;
    const items = [...this.state.items];
    let running = this.state.running;
    switch (event.name) {
      case 'text': {
        const last = items.at(-1);
        if (last?.kind === 'reply') items[items.length - 1] = { kind: 'reply', text: last.text + event.data.delta };
        else items.push({ kind: 'reply', text: event.data.delta });
        break;
      }
      case 'tool_started':
        items.push({ kind: 'tool', ...event.data, ok: null });
        break;
      case 'tool_finished': {
        const at = items.findIndex((i) => i.kind === 'tool' && i.callId === event.data.callId);
        const tool = items[at];
        if (tool?.kind === 'tool') items[at] = { ...tool, ok: event.data.ok };
        break;
      }
      case 'client_tool_call':
        this.run(event.data);
        break;
      case 'turn_ended':
        items.push({ kind: 'end', reason: event.data.reason, category: event.data.category });
        running = false;
        break;
      case 'error':
        this.update({ ...this.state, problem: { code: 'provider', detail: event.data.detail ?? event.data.title } });
        break;
    }
    this.update({ ...this.state, items, running, lastEventId: event.id });
    if (event.name === 'turn_ended') void this.settle();
  }

  private run(call: AssistantEvents['client_tool_call']): void {
    const id = this.state.id;
    if (!id || this.answered.has(call.callId)) return;
    this.answered.add(call.callId);
    void this.tools
      .run(call.name, call.input)
      .then((outcome) => this.client.answer(id, { callId: call.callId, ...outcome }))
      .catch(() => undefined);
  }

  /** Once a turn ends, the read settles anything the stream missed (a ring overrun), then the stream closes if unwanted. */
  private async settle(): Promise<void> {
    const id = this.state.id;
    const sent = this.sends;
    const view = id ? await this.client.read(id).catch(() => null) : null;
    if (view && sent === this.sends && view.id === this.state.id && view.lastEventId >= this.state.lastEventId) {
      this.update({ ...this.state, items: view.items, running: view.running, lastEventId: view.lastEventId });
    }
    this.settleStream();
  }

  private async lost(): Promise<void> {
    this.stopStream();
    const id = this.state.id;
    const sent = this.sends;
    const view = id ? await this.client.read(id).catch(() => null) : null;
    // A send, a new conversation or a start-over since the read began makes its answer someone else's.
    if (sent !== this.sends || id !== this.state.id) return;
    if (view) {
      this.update({ ...this.state, items: view.items, running: view.running, lastEventId: view.lastEventId });
      this.settleStream();
      for (const call of view.pendingCalls) this.run(call);
      return;
    }
    this.ids.set(null);
    this.update({ ...this.state, id: null, running: false, lastEventId: 0, problem: { code: 'gone', detail: null } });
  }

  private update(state: ConversationState): void {
    this.state = state;
    this.onChange(state);
  }
}
