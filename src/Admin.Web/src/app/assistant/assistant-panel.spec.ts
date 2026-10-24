import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EMPTY_CONVERSATION, ConversationState } from './assistant-client';
import { AssistantPanel, END_REASON_TEXT, STATUS_REASON_TEXT, toolLabel } from './assistant-panel';
import { AssistantService } from './assistant-service';
import { AssistantStatus, STATUS_REASONS, TURN_END_REASONS } from './protocol';

function stub() {
  const added = signal<string[]>([]);
  return {
    added,
    status: signal<AssistantStatus | null>({ enabled: true, provider: 'fake', model: 'scripted', reason: null }),
    state: signal<ConversationState>(EMPTY_CONVERSATION),
    panelOpen: signal(true),
    needsNotice: signal(false),
    acknowledge: vi.fn(),
    identity: signal<string | null>(null),
    users: signal<string[]>([]),
    loadUsers: vi.fn(),
    pin: vi.fn(),
    addProposed: vi.fn((_type: string, callId: string): 'added' | 'no-room' | 'no-page' => {
      added.update((a) => [...a, callId]);
      return 'added';
    }),
    send: vi.fn(() => Promise.resolve()),
    cancel: vi.fn(() => Promise.resolve()),
    startOver: vi.fn(() => Promise.resolve()),
  };
}

async function render(assistant = stub()) {
  TestBed.configureTestingModule({ providers: [{ provide: AssistantService, useValue: assistant }] });
  const fixture = TestBed.createComponent(AssistantPanel);
  await fixture.whenStable();
  return { fixture, assistant, element: fixture.nativeElement as HTMLElement };
}

describe('AssistantPanel', () => {
  it('names the provider the questions go to', async () => {
    const { element } = await render();
    expect(element.querySelector('.provider')?.textContent).toContain('fake');
  });

  it('shows no notice for a provider that sends nothing out', async () => {
    const { element } = await render();
    expect(element.querySelector('.notice')).toBeNull();
  });

  it('holds the first message behind a notice that says where the questions go, until it is accepted', async () => {
    const assistant = stub();
    assistant.status.set({ enabled: true, provider: 'anthropic', model: 'claude-opus-5-5', reason: null });
    assistant.needsNotice.set(true);
    const { fixture, element } = await render(assistant);
    fixture.componentInstance.draft.set('where are the logs?');
    await fixture.whenStable();

    expect(element.querySelector('.notice')?.textContent).toContain('go to anthropic');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);

    (element.querySelector('.notice button') as HTMLButtonElement).click();
    expect(assistant.acknowledge).toHaveBeenCalledTimes(1);

    assistant.needsNotice.set(false);
    await fixture.whenStable();

    expect(element.querySelector('.notice')).toBeNull();
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(false);
  });

  it('offers no identity choice until the host has named the realm users', async () => {
    const { element } = await render();

    expect(element.querySelector('select')).toBeNull();
  });

  it('shows the pinned user chosen when the list arrives after the pin was set', async () => {
    const assistant = stub();
    assistant.identity.set('browser');
    const { fixture, element } = await render(assistant);
    expect(element.querySelector('select')).toBeNull();

    assistant.users.set(['demo', 'browser']);
    await fixture.whenStable();

    expect(element.querySelector('select')!.value).toBe('browser');
  });

  it('pins the realm user the platform reads are made as, and takes the pin off again', async () => {
    const assistant = stub();
    assistant.users.set(['demo', 'browser']);
    assistant.identity.set('demo');
    const { fixture, element } = await render(assistant);
    const select = element.querySelector('select')!;

    expect(Array.from(select.options).map((o) => o.value)).toEqual(['', 'demo', 'browser']);
    expect(select.value).toBe('demo');

    select.value = 'browser';
    select.dispatchEvent(new Event('change'));
    expect(assistant.pin).toHaveBeenLastCalledWith('browser');

    select.value = '';
    select.dispatchEvent(new Event('change'));
    expect(assistant.pin).toHaveBeenLastCalledWith(null);
    expect(assistant.loadUsers).toHaveBeenCalledTimes(1);
    await fixture.whenStable();
  });

  it('keeps the draft when Enter is pressed before the notice is accepted', async () => {
    const assistant = stub();
    assistant.needsNotice.set(true);
    const { fixture, element } = await render(assistant);
    const box = element.querySelector('textarea')!;

    box.value = 'where are the logs?';
    box.dispatchEvent(new Event('input'));
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await fixture.whenStable();

    expect(assistant.send).not.toHaveBeenCalled();
    expect(fixture.componentInstance.draft()).toBe('where are the logs?');
  });

  it('shows the reply as it streams, and each tool call by its label', async () => {
    const { fixture, assistant, element } = await render();

    assistant.state.set({
      ...EMPTY_CONVERSATION,
      id: 'c1',
      running: true,
      items: [
        { kind: 'operator', text: 'where are the logs?' },
        { kind: 'reply', text: 'Opening ' },
      ],
    });
    await fixture.whenStable();
    expect(element.querySelector('.reply')?.textContent).toBe('Opening ');

    assistant.state.update((s) => ({
      ...s,
      items: [
        ...s.items.slice(0, 1),
        { kind: 'reply', text: 'Opening Logs. ' },
        { kind: 'tool', callId: 'k', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: null },
      ],
    }));
    await fixture.whenStable();

    expect(element.querySelector('.reply')?.textContent).toBe('Opening Logs. ');
    expect(element.querySelector('.tool')?.textContent).toContain('Opening Logs');
  });

  const offered = {
    kind: 'tool',
    callId: 'k-note',
    name: 'propose_widget',
    side: 'client',
    args: { type: 'note' },
    ok: true,
  } as const;

  it('offers an Add button for a widget the assistant proposed, and adds it only on the click', async () => {
    const assistant = stub();
    assistant.state.set({
      ...EMPTY_CONVERSATION,
      id: 'c1',
      items: [{ kind: 'operator', text: 'a scratch pad?' }, offered],
    });
    const { fixture, element } = await render(assistant);
    const button = element.querySelector<HTMLButtonElement>('.propose')!;

    expect(button.textContent).toContain('Add Note');
    expect(assistant.addProposed).not.toHaveBeenCalled();

    button.click();
    await fixture.whenStable();

    expect(assistant.addProposed).toHaveBeenCalledWith('note', 'k-note');
    expect(element.querySelector<HTMLButtonElement>('.propose')!.textContent).toContain('Added Note');
    expect(element.querySelector<HTMLButtonElement>('.propose')!.disabled).toBe(true);
  });

  it('says when the page has no room for a proposed widget', async () => {
    const assistant = stub();
    assistant.addProposed.mockReturnValue('no-room');
    assistant.state.set({ ...EMPTY_CONVERSATION, id: 'c1', items: [offered] });
    const { fixture, element } = await render(assistant);

    element.querySelector<HTMLButtonElement>('.propose')!.click();
    await fixture.whenStable();

    // Refused, not taken: the button stays live to be pressed again once there is room.
    expect(element.querySelector('.propose')!.textContent).toContain('No room on this page for Note');
    expect(element.querySelector<HTMLButtonElement>('.propose')!.disabled).toBe(false);
  });

  it('says to open a workspace page when none is showing, which is not the same as no room', async () => {
    const assistant = stub();
    assistant.addProposed.mockReturnValue('no-page');
    assistant.state.set({ ...EMPTY_CONVERSATION, id: 'c1', items: [offered] });
    const { fixture, element } = await render(assistant);

    element.querySelector<HTMLButtonElement>('.propose')!.click();
    await fixture.whenStable();

    expect(element.querySelector('.propose')!.textContent).toContain('Open a workspace page to add Note');
  });

  it('does not offer again a widget the operator already added, as when the conversation is redrawn after a reload', async () => {
    const assistant = stub();
    assistant.added.set(['k-note']);
    assistant.state.set({ ...EMPTY_CONVERSATION, id: 'c1', items: [offered] });
    const { element } = await render(assistant);

    expect(element.querySelector('.propose')!.textContent).toContain('Added Note');
    expect(element.querySelector<HTMLButtonElement>('.propose')!.disabled).toBe(true);
  });

  it('offers no button for a proposal that failed, or for any other tool', async () => {
    const assistant = stub();
    assistant.state.set({
      ...EMPTY_CONVERSATION,
      id: 'c1',
      items: [
        { ...offered, callId: 'k-bad', args: { type: 'hologram' }, ok: false },
        { kind: 'tool', callId: 'k-open', name: 'open_page', side: 'client', args: { page: 'Broker' }, ok: true },
      ],
    });
    const { element } = await render(assistant);

    expect(element.querySelector('.propose')).toBeNull();
  });

  it('says plainly that a turn was refused rather than showing an empty reply', async () => {
    const { fixture, assistant, element } = await render();

    assistant.state.set({
      ...EMPTY_CONVERSATION,
      id: 'c1',
      items: [
        { kind: 'operator', text: 'help' },
        { kind: 'end', reason: 'refused', category: null },
      ],
    });
    await fixture.whenStable();

    expect(element.querySelector('.end.refused')?.textContent).toBe(END_REASON_TEXT.refused);
    expect(element.querySelector('.reply')).toBeNull();
  });

  it('sends the draft on Enter and offers Stop while a turn runs', async () => {
    const { fixture, assistant, element } = await render();
    const box = element.querySelector('textarea')!;

    box.value = 'where are the logs?';
    box.dispatchEvent(new Event('input'));
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await fixture.whenStable();
    expect(assistant.send).toHaveBeenCalledWith('where are the logs?');

    assistant.state.set({ ...EMPTY_CONVERSATION, id: 'c1', running: true });
    await fixture.whenStable();
    const stop = [...element.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Stop')!;
    stop.click();
    expect(assistant.cancel).toHaveBeenCalled();
  });

  it('holds no timer: the event stream, not a poll, keeps it current', async () => {
    // The test environment keeps intervals of its own; only one this console's code started counts.
    const original = globalThis.setInterval;
    const ours: string[] = [];
    const intervals = vi.spyOn(globalThis, 'setInterval').mockImplementation(((handler: TimerHandler, ms?: number) => {
      const stack = new Error().stack ?? '';
      if (stack.split('\n').some((frame) => frame.includes('/src/app/') && !frame.includes('.spec.ts')))
        ours.push(stack);
      return original(handler, ms);
    }) as typeof setInterval);
    try {
      const { fixture, assistant } = await render();
      assistant.state.set({ ...EMPTY_CONVERSATION, id: 'c1', items: [{ kind: 'reply', text: 'Hi' }] });
      assistant.panelOpen.set(false);
      await fixture.whenStable();
      expect(ours).toEqual([]);
    } finally {
      intervals.mockRestore();
    }
  });
});

// Every code a client receives has words in the console's language (§5.4); i18n:check holds each to a translation.
describe('the assistant codes', () => {
  it('words every end reason but done, whose reply is the answer', () => {
    expect(Object.keys(END_REASON_TEXT).sort()).toEqual([...TURN_END_REASONS].sort());
    for (const reason of TURN_END_REASONS) expect(END_REASON_TEXT[reason] !== '').toBe(reason !== 'done');
  });

  it('words every status reason', () => {
    expect(Object.keys(STATUS_REASON_TEXT).sort()).toEqual([...STATUS_REASONS].sort());
    for (const reason of STATUS_REASONS) expect(STATUS_REASON_TEXT[reason]).not.toBe('');
  });

  it('labels a screen opening by the screen as labelled', () => {
    expect(toolLabel('open_screen', { path: 'requests' })).toBe('Opening API');
  });

  it('labels the workspace tools by the page, widget or id they name', () => {
    expect(toolLabel('open_page', { page: 'Broker' })).toBe('Opening Broker');
    expect(toolLabel('highlight_widget', { widget: 'widget:queues' })).toBe('Showing Queues');
    expect(toolLabel('propose_widget', { type: 'note' })).toBe('Offering Note');
    expect(toolLabel('follow_correlation', { correlationId: 'order-1' })).toBe('Following order-1');
  });

  it("labels a call to one of the host's reads in the console's words, and any other tool by its name", () => {
    expect(toolLabel('stack_status', {})).toBe('Reading the stack');
    expect(toolLabel('list_runbooks', {})).toBe('Reading the runbooks');
    expect(toolLabel('from_a_newer_host', {})).toBe('Using from_a_newer_host');
    expect(toolLabel('toString', {})).toBe('Using toString');
  });
});
