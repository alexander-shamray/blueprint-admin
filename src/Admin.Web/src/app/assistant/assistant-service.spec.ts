import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../core/host/host-client';
import { WorkspaceService } from '../workspace/workspace-service';
import { SCREENS } from '../screens';
import { WIDGETS } from '../workspace/widgets';
import { ACKNOWLEDGED_KEY, AssistantService, IDENTITY_KEY, catalogue } from './assistant-service';
import { AssistantStatus } from './protocol';

const anthropic: AssistantStatus = { enabled: true, provider: 'anthropic', model: 'claude-opus-5-5', reason: null };

function service(workspace: Record<string, unknown> = {}): AssistantService {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: Router, useValue: { url: '/', navigate: vi.fn(() => Promise.resolve(true)) } },
      {
        provide: WorkspaceService,
        useValue: {
          currentPage: () => null,
          currentPageId: () => 'p1',
          editing: signal(false),
          addWidget: () => true,
          ...workspace,
        },
      },
      {
        provide: HostClient,
        useValue: { identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])) },
      },
    ],
  });
  return TestBed.inject(AssistantService);
}

describe('AssistantService notice (AI assistant design §10)', () => {
  let fetched: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    fetched = vi.fn(() => Promise.resolve(new Response(null, { status: 404 })));
    vi.stubGlobal('fetch', fetched);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('needs none while the assistant is off, unread, or a provider that sends nothing out', () => {
    const assistant = service();
    expect(assistant.needsNotice()).toBe(false);

    assistant.status.set({ enabled: false, provider: null, model: null, reason: 'off' });
    expect(assistant.needsNotice()).toBe(false);

    assistant.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });
    expect(assistant.needsNotice()).toBe(false);
  });

  it('is needed for a provider that is sent the questions, and for one it does not know', () => {
    const assistant = service();

    assistant.status.set(anthropic);
    expect(assistant.needsNotice()).toBe(true);

    assistant.status.set({ ...anthropic, provider: 'something-new' });
    expect(assistant.needsNotice()).toBe(true);
  });

  it('holds the first message until the notice is accepted, and sends nothing meanwhile', async () => {
    const assistant = service();
    assistant.status.set(anthropic);

    await assistant.send('where are the logs?');

    expect(fetched).not.toHaveBeenCalled();
  });

  it('lets the message through once the notice is accepted', async () => {
    const assistant = service();
    assistant.status.set(anthropic);

    assistant.acknowledge();
    await assistant.send('where are the logs?');

    expect(assistant.needsNotice()).toBe(false);
    expect(fetched).toHaveBeenCalled();
  });

  it('pins the realm user for the tab, sends it as hostOptions.identity, and lets the host choose again once it is taken off', async () => {
    sessionStorage.clear();
    const assistant = service();
    assistant.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });

    assistant.pin('browser');
    expect(sessionStorage.getItem(IDENTITY_KEY)).toBe('browser');
    expect(service().identity()).toBe('browser');

    const pinned = service();
    pinned.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });
    await pinned.send('is the stack running?');
    expect(JSON.parse(fetched.mock.calls[0][1].body as string).hostOptions).toEqual({ identity: 'browser' });

    pinned.pin(null);
    expect(sessionStorage.getItem(IDENTITY_KEY)).toBeNull();
    fetched.mockClear();
    const automatic = service();
    automatic.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });
    await automatic.send('is the stack running?');
    // Not null: the host keeps an open conversation's options from the turn before when a turn sends none.
    expect(JSON.parse(fetched.mock.calls[0][1].body as string).hostOptions).toEqual({});
  });

  it('drops a pin the host no longer lists once the users arrive', () => {
    sessionStorage.setItem(IDENTITY_KEY, 'mallory');
    const assistant = service();
    expect(assistant.identity()).toBe('mallory');

    assistant.loadUsers();

    expect(assistant.identity()).toBeNull();
    expect(sessionStorage.getItem(IDENTITY_KEY)).toBeNull();
  });

  it('does not send a pin the host has said it does not list, while the panel has not yet dropped it', async () => {
    const assistant = service();
    assistant.status.set({ enabled: true, provider: 'fake', model: 'scripted', reason: null });
    assistant.pin('mallory');
    assistant.users.set(['demo', 'browser']);

    await assistant.send('is the stack running?');

    expect(JSON.parse(fetched.mock.calls[0][1].body as string).hostOptions).toEqual({});
  });

  it('keeps a pin the host lists', () => {
    sessionStorage.setItem(IDENTITY_KEY, 'browser');
    const assistant = service();

    assistant.loadUsers();

    expect(assistant.identity()).toBe('browser');
  });

  it('lists the realm users the host names, usernames only', () => {
    const assistant = service();

    assistant.loadUsers();

    expect(assistant.users()).toEqual(['demo', 'browser']);
  });

  it('keeps the acceptance for the browser and per provider', () => {
    const first = service();
    first.status.set(anthropic);
    first.acknowledge();

    expect(JSON.parse(localStorage.getItem(ACKNOWLEDGED_KEY)!)).toEqual(['anthropic']);

    // A new load of the console, in the same browser.
    const again = service();
    again.status.set(anthropic);
    expect(again.needsNotice()).toBe(false);

    // Another provider is a new question.
    again.status.set({ ...anthropic, provider: 'other' });
    expect(again.needsNotice()).toBe(true);
  });

  it('asks again when the browser cannot keep the answer, and does not fail', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage is blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage is blocked');
    });
    const assistant = service();
    assistant.status.set(anthropic);

    expect(() => assistant.acknowledge()).not.toThrow();
    // Accepted for this load, and asked again at the next.
    expect(assistant.needsNotice()).toBe(false);
  });

  it('ignores a stored value that is not a list of provider ids', () => {
    localStorage.setItem(ACKNOWLEDGED_KEY, '{"anthropic":true}');
    const assistant = service();
    assistant.status.set(anthropic);

    expect(assistant.needsNotice()).toBe(true);
  });
});

describe('AssistantService.show (an Assistant widget is a view of the conversation)', () => {
  it('wants the conversation stream while a widget shows, as it does while the panel is open, and not after', () => {
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    const assistant = service();
    const setVisible = vi.spyOn(assistant['conversation'], 'setVisible');
    let destroyed = (): void => undefined;

    TestBed.tick();
    expect(setVisible).toHaveBeenLastCalledWith(false);

    assistant.show({ onDestroy: (callback: () => void) => (destroyed = callback) } as never);
    TestBed.tick();
    expect(setVisible).toHaveBeenLastCalledWith(true);

    destroyed();
    TestBed.tick();
    expect(setVisible).toHaveBeenLastCalledWith(false);
  });
});

describe('AssistantService.addProposed (the operator accepts a proposed widget)', () => {
  beforeEach(() => sessionStorage.clear());

  it('adds the widget at its default size and preset to the page being shown, and remembers the offer was taken', () => {
    const addWidget = vi.fn(() => true);
    const editing = signal(false);
    const assistant = service({ addWidget, editing });

    expect(assistant.addProposed('widget:note', 'k1')).toBe('added');

    expect(addWidget).toHaveBeenCalledWith('p1', {
      type: 'note',
      cols: 12,
      rows: 12,
      config: { source: '' },
    });
    expect(editing()).toBe(true);
    expect(assistant.added()).toEqual(['k1']);
    expect(service().added()).toEqual(['k1']);
  });

  it('says there is no room, leaves the layout editor off and does not count the offer as taken', () => {
    const editing = signal(false);
    const assistant = service({ addWidget: () => false, editing });

    expect(assistant.addProposed('note', 'k1')).toBe('no-room');

    expect(editing()).toBe(false);
    expect(assistant.added()).toEqual([]);
  });

  it('says there is no page when none is showing or the type is none the console knows', () => {
    const addWidget = vi.fn(() => true);

    expect(service({ currentPageId: () => null, addWidget }).addProposed('note', 'k1')).toBe('no-page');
    expect(service({ addWidget }).addProposed('hologram', 'k2')).toBe('no-page');
    expect(addWidget).not.toHaveBeenCalled();
  });
});

describe('the catalogue the assistant explains (AI assistant design §6.1)', () => {
  it('lists every screen, then every widget type, each once, with the text its owner wrote', () => {
    const entries = catalogue();

    expect(entries).toHaveLength(SCREENS.length + WIDGETS.length);
    expect(new Set(entries.map((e) => e.id)).size).toBe(entries.length);
    expect(entries.slice(0, SCREENS.length).map((e) => e.id)).toEqual(SCREENS.map((s) => s.path));
    expect(entries.every((e) => e.label !== '' && e.where !== '' && e.help !== '')).toBe(true);
    for (const widget of WIDGETS) {
      expect(entries.find((e) => e.id === `widget:${widget.type}`)?.help).toBe(widget.help);
    }
  });

  it('says which built-in page holds a widget, and that one no page holds is added by hand', () => {
    const entries = catalogue();

    expect(entries.find((e) => e.id === 'widget:queues')?.where).toBe('/broker');
    expect(entries.find((e) => e.id === 'widget:job-output')?.where).toBe('/stack');
    expect(entries.find((e) => e.id === 'widget:platform-diagram')?.where).toContain('Add widget');
  });
});
