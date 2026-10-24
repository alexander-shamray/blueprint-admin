import { AssistantClient, AssistantConversation, ConversationState, IdStore } from './assistant-client';
import { FakeHost, json, settle } from './assistant-testing';
import { ClientToolRegistry, viewTool } from './client-tools';
import { ConversationView, OpenConversation } from './protocol';

const OPENING: OpenConversation = {
  client: { name: 'test', version: '1' },
  locale: 'en-US',
  catalogue: [{ id: 'logs', label: 'Logs', where: '/logs', help: 'Log lines.' }],
  clientTools: [],
  hostOptions: null,
};

function memoryIds(initial: string | null = null): IdStore & { id: string | null } {
  const store = {
    id: initial,
    get: () => store.id,
    set: (id: string | null) => {
      store.id = id;
    },
  };
  return store;
}

function setup(ids = memoryIds()) {
  const host = new FakeHost()
    .on('POST', /\/conversations$/, () => json({ id: 'c1' }))
    .on('POST', /\/turns$/, () => new Response(null, { status: 202 }))
    .on('POST', /\/tool-results$/, () => new Response(null, { status: 204 }))
    .on('POST', /\/cancel$/, () => new Response(null, { status: 204 }));
  const tools = new ClientToolRegistry();
  const opened: string[] = [];
  tools.register({
    descriptor: viewTool('open_screen', 'Open a screen', 'Opens one.', { type: 'object' }),
    run: (input) => {
      opened.push(String(input['path']));
      return { ok: true, content: `opened /${String(input['path'])}` };
    },
  });
  const states: ConversationState[] = [];
  const client = new AssistantClient('/api/assistant', host.fetch, host.openStream);
  const conversation = new AssistantConversation(
    client,
    tools,
    () => OPENING,
    ids,
    (s) => states.push(s),
  );
  return { host, conversation, states, opened, ids, tools };
}

describe('AssistantConversation', () => {
  it('opens a conversation on the first message, keeps its id for the tab and follows its stream', async () => {
    const { host, conversation, ids } = setup();

    await conversation.send('where are the logs?', { page: 'Stack' }, 'en-US');

    expect(ids.id).toBe('c1');
    expect(host.requests.find((r) => r.url.endsWith('/conversations'))?.body).toEqual(OPENING);
    expect(host.requests.find((r) => r.url.endsWith('/turns'))?.body).toEqual({
      text: 'where are the logs?',
      context: { page: 'Stack' },
      locale: 'en-US',
      hostOptions: null,
    });
    expect(host.stream.url).toBe('/api/assistant/conversations/c1/events?after=0');
    expect(conversation.current.running).toBe(true);
    expect(conversation.current.items).toEqual([{ kind: 'operator', text: 'where are the logs?' }]);
  });

  it('sends the host options with each turn, an empty object once nothing is pinned', async () => {
    const { host, conversation } = setup();

    await conversation.send('one', null, 'en-US', { identity: 'browser' });
    host.stream.emit('turn_ended', { reason: 'done', category: null }, 1);
    await settle();
    await conversation.send('two', null, 'en-US', {});

    const turns = host.requests
      .filter((r) => r.url.endsWith('/turns'))
      .map((r) => (r.body as { hostOptions: unknown }).hostOptions);
    expect(turns).toEqual([{ identity: 'browser' }, {}]);
  });

  it('runs the client calls of a round one after another, in the order they came', async () => {
    const { host, conversation, tools } = setup();
    const order: string[] = [];
    let release = (): void => undefined;
    tools.register({
      descriptor: viewTool('slow', 'Slow', 'Takes a while.', { type: 'object' }),
      run: () =>
        new Promise((resolve) => {
          order.push('slow:start');
          release = () => {
            order.push('slow:end');
            resolve({ ok: true, content: '' });
          };
        }),
    });
    tools.register({
      descriptor: viewTool('quick', 'Quick', 'Does not.', { type: 'object' }),
      run: () => {
        order.push('quick');
        return { ok: true, content: '' };
      },
    });
    await conversation.send('go', null, 'en-US');

    host.stream.emit('client_tool_call', { callId: 'a', name: 'slow', input: {} }, 1);
    host.stream.emit('client_tool_call', { callId: 'b', name: 'quick', input: {} }, 2);
    await settle();
    expect(order).toEqual(['slow:start']);

    release();
    await settle();
    expect(order).toEqual(['slow:start', 'slow:end', 'quick']);
    expect(
      host.requests.filter((r) => r.url.endsWith('/tool-results')).map((r) => (r.body as { callId: string }).callId),
    ).toEqual(['a', 'b']);
  });

  it('streams the reply, runs a client tool, posts its result and ends', async () => {
    const { host, conversation, opened } = setup();
    host.on('GET', /\/conversations\/c1$/, () =>
      json({
        id: 'c1',
        items: [
          { kind: 'operator', text: 'logs?' },
          { kind: 'reply', text: 'Opening Logs. ' },
          { kind: 'tool', callId: 'k1', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: true },
          { kind: 'reply', text: 'Logs is open.' },
          { kind: 'end', reason: 'done', category: null },
        ],
        running: false,
        lastEventId: 7,
        pendingCalls: [],
      } satisfies ConversationView),
    );
    await conversation.send('logs?', null, 'en-US');
    const stream = host.stream;

    stream.emit('text', { delta: 'Opening ' }, 1);
    stream.emit('text', { delta: 'Logs. ' }, 2);
    stream.emit('tool_started', { callId: 'k1', name: 'open_screen', side: 'client', args: { path: 'logs' } }, 3);
    stream.emit('client_tool_call', { callId: 'k1', name: 'open_screen', input: { path: 'logs' } }, 4);
    await settle();

    expect(opened).toEqual(['logs']);
    expect(host.requests.find((r) => r.url.endsWith('/tool-results'))?.body).toEqual({
      callId: 'k1',
      ok: true,
      content: 'opened /logs',
    });
    expect(conversation.current.items.slice(1)).toEqual([
      { kind: 'reply', text: 'Opening Logs. ' },
      { kind: 'tool', callId: 'k1', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: null },
    ]);

    stream.emit('tool_finished', { callId: 'k1', ok: true }, 5);
    stream.emit('text', { delta: 'Logs is open.' }, 6);
    stream.emit(
      'turn_ended',
      {
        reason: 'done',
        category: null,
        usage: { input: 1, output: 1, cached: 0 },
        total: { input: 1, output: 1, cached: 0 },
      },
      7,
    );
    await settle();

    expect(conversation.current.running).toBe(false);
    expect(conversation.current.lastEventId).toBe(7);
    expect(conversation.current.items.at(-1)).toEqual({ kind: 'end', reason: 'done', category: null });
    // Idle and nothing visible: the stream is closed, and nothing is left open.
    expect(stream.closed).toBe(true);
  });

  it('answers a client tool call once, even when it arrives again under a later id', async () => {
    const { host, conversation, opened } = setup();
    await conversation.send('logs?', null, 'en-US');

    host.stream.emit('client_tool_call', { callId: 'k1', name: 'open_screen', input: { path: 'logs' } }, 1);
    host.stream.emit('client_tool_call', { callId: 'k1', name: 'open_screen', input: { path: 'logs' } }, 2);
    await settle();

    expect(opened).toEqual(['logs']);
  });

  it('after a reload answers a client call the read says is still waiting', async () => {
    const { host, conversation, opened } = setup(memoryIds('c1'));
    host.on('GET', /\/conversations\/c1$/, () =>
      json({
        id: 'c1',
        items: [{ kind: 'tool', callId: 'k1', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: null }],
        running: true,
        lastEventId: 3,
        pendingCalls: [{ callId: 'k1', name: 'open_screen', input: { path: 'logs' } }],
      } satisfies ConversationView),
    );

    await conversation.resume();
    await settle();

    expect(opened).toEqual(['logs']);
    expect(host.requests.find((r) => r.url.endsWith('/tool-results'))?.body).toEqual({
      callId: 'k1',
      ok: true,
      content: 'opened /logs',
    });
  });

  it('a read begun before a new message never undoes it', async () => {
    const { host, conversation } = setup();
    // The read a turn's end starts answers as the host stood then: idle, and without the next message.
    host.on('GET', /\/conversations\/c1$/, () =>
      json({ id: 'c1', items: [], running: false, lastEventId: 1, pendingCalls: [] }),
    );
    await conversation.send('one', null, 'en-US');

    host.stream.emit(
      'turn_ended',
      {
        reason: 'done',
        category: null,
        usage: { input: 0, output: 0, cached: 0 },
        total: { input: 0, output: 0, cached: 0 },
      },
      1,
    );
    // Sent before that read has come back.
    await conversation.send('two', null, 'en-US');
    await settle();

    expect(conversation.current.running).toBe(true);
    expect(conversation.current.items.at(-1)).toEqual({ kind: 'operator', text: 'two' });
  });

  it('when the browser gives the stream up and the host still holds the conversation, redraws and follows on', async () => {
    const { host, conversation, opened } = setup();
    await conversation.send('logs?', null, 'en-US');
    host.on('GET', /\/conversations\/c1$/, () =>
      json({
        id: 'c1',
        items: [
          { kind: 'operator', text: 'logs?' },
          { kind: 'tool', callId: 'k1', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: null },
        ],
        running: true,
        lastEventId: 5,
        pendingCalls: [{ callId: 'k1', name: 'open_screen', input: { path: 'logs' } }],
      } satisfies ConversationView),
    );

    host.stream.lose();
    await settle();

    expect(conversation.current.items.at(-1)).toEqual({
      kind: 'tool',
      callId: 'k1',
      name: 'open_screen',
      side: 'client',
      args: { path: 'logs' },
      ok: null,
    });
    expect(host.stream.url).toBe('/api/assistant/conversations/c1/events?after=5');
    expect(opened).toEqual(['logs']);
  });

  it('a read of a conversation started over since is dropped', async () => {
    const { host, conversation, ids } = setup();
    await conversation.send('logs?', null, 'en-US');
    host.on('GET', /\/conversations\/c1$/, () =>
      json({ id: 'c1', items: [{ kind: 'operator', text: 'logs?' }], running: true, lastEventId: 5, pendingCalls: [] }),
    );

    host.stream.lose();
    await conversation.startOver();
    await settle();

    expect(ids.id).toBeNull();
    expect(conversation.current.items).toEqual([]);
  });

  it('when the browser gives the stream up and the host has forgotten the conversation, the next message starts a new one', async () => {
    const { host, conversation, ids } = setup();
    await conversation.send('logs?', null, 'en-US');

    host.stream.lose();
    await settle();

    expect(ids.id).toBeNull();
    expect(conversation.current.running).toBe(false);
    expect(conversation.current.problem).toEqual({ code: 'gone', detail: null });
  });

  it('after a reload redraws from the conversation read and follows on from its lastEventId', async () => {
    const ids = memoryIds('c1');
    const { host, conversation } = setup(ids);
    host.on('GET', /\/conversations\/c1$/, () =>
      json({
        id: 'c1',
        items: [
          { kind: 'operator', text: 'where?' },
          { kind: 'reply', text: 'Open' },
        ],
        running: true,
        lastEventId: 12,
        pendingCalls: [],
      }),
    );

    await conversation.resume();

    expect(conversation.current.items).toEqual([
      { kind: 'operator', text: 'where?' },
      { kind: 'reply', text: 'Open' },
    ]);
    expect(host.stream.url).toBe('/api/assistant/conversations/c1/events?after=12');

    host.stream.emit('text', { delta: 'ing Logs.' }, 12);
    host.stream.emit('text', { delta: 'ing Logs.' }, 13);
    expect(conversation.current.items.at(-1)).toEqual({ kind: 'reply', text: 'Opening Logs.' });
  });

  it('forgets an id the host no longer holds', async () => {
    const ids = memoryIds('gone');
    const { host, conversation } = setup(ids);

    await conversation.resume();

    expect(ids.id).toBeNull();
    expect(host.streams).toEqual([]);
  });

  it('holds no stream while idle and hidden, and opens one while visible', async () => {
    const ids = memoryIds('c1');
    const { host, conversation } = setup(ids);
    host.on('GET', /\/conversations\/c1$/, () =>
      json({ id: 'c1', items: [], running: false, lastEventId: 3, pendingCalls: [] }),
    );
    await conversation.resume();
    expect(host.streams).toEqual([]);

    conversation.setVisible(true);
    expect(host.stream.url).toBe('/api/assistant/conversations/c1/events?after=3');

    conversation.setVisible(false);
    expect(host.streams[0].closed).toBe(true);
  });

  it('keeps the stream while a turn runs, though nothing shows it', async () => {
    const { host, conversation } = setup();
    conversation.setVisible(true);
    await conversation.send('logs?', null, 'en-US');

    conversation.setVisible(false);

    expect(host.stream.closed).toBe(false);
  });

  it('says a turn is running when the host refuses a second one, and drops the unsent message', async () => {
    const { host, conversation } = setup();
    host.on('POST', /\/turns$/, () => json({ title: 'A turn is running' }, 409));

    await conversation.send('again', null, 'en-US');

    expect(conversation.current.problem).toEqual({ code: 'turn-running', detail: null });
    expect(conversation.current.items).toEqual([]);
  });

  it('sends the same words to a new conversation when the host has forgotten the old one', async () => {
    const ids = memoryIds();
    const { host, conversation } = setup(ids);
    await conversation.send('one', null, 'en-US');
    host.stream.emit(
      'turn_ended',
      {
        reason: 'done',
        category: null,
        usage: { input: 0, output: 0, cached: 0 },
        total: { input: 0, output: 0, cached: 0 },
      },
      1,
    );
    await settle();

    let turns = 0;
    host.on('POST', /\/conversations\/c1\/turns$/, () => new Response(null, { status: 404 }));
    host.on('POST', /\/conversations$/, () => json({ id: 'c2' }));
    host.on('POST', /\/conversations\/c2\/turns$/, () => {
      turns++;
      return new Response(null, { status: 202 });
    });

    await conversation.send('two', null, 'en-US');

    expect(ids.id).toBe('c2');
    expect(turns).toBe(1);
    expect(host.stream.url).toBe('/api/assistant/conversations/c2/events?after=0');
  });

  it('shows a refusal as its own end, not an empty reply', async () => {
    const { host, conversation } = setup();
    await conversation.send('help', null, 'en-US');

    host.stream.emit(
      'turn_ended',
      {
        reason: 'refused',
        category: 'cyber',
        usage: { input: 0, output: 0, cached: 0 },
        total: { input: 0, output: 0, cached: 0 },
      },
      1,
    );

    expect(conversation.current.items.at(-1)).toEqual({ kind: 'end', reason: 'refused', category: 'cyber' });
  });

  it('cancels the running turn on the host', async () => {
    const { host, conversation } = setup();
    await conversation.send('logs?', null, 'en-US');

    await conversation.cancel();

    expect(host.requests.at(-1)).toEqual({
      method: 'POST',
      url: '/api/assistant/conversations/c1/cancel',
      body: undefined,
    });
  });

  it('starting over forgets the conversation on the host and in the tab', async () => {
    const ids = memoryIds();
    const { host, conversation } = setup(ids);
    await conversation.send('logs?', null, 'en-US');

    await conversation.startOver();

    expect(ids.id).toBeNull();
    expect(conversation.current.items).toEqual([]);
    expect(host.requests.at(-1)?.method).toBe('DELETE');
  });
});

describe('AssistantClient', () => {
  it('reads the status, and null when the host cannot say', async () => {
    const host = new FakeHost().on('GET', /\/api\/assistant$/, () =>
      json({ enabled: false, provider: null, model: null, reason: 'off' }),
    );
    expect(await new AssistantClient('/api/assistant', host.fetch).status()).toEqual({
      enabled: false,
      provider: null,
      model: null,
      reason: 'off',
    });

    const failing = new AssistantClient('/api/assistant', () => Promise.reject(new Error('offline')));
    expect(await failing.status()).toBeNull();
  });
});
