import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { HostClient } from '../core/host/host-client';
import { WorkspaceService } from '../workspace/workspace-service';
import { ACKNOWLEDGED_KEY, AssistantService, IDENTITY_KEY } from './assistant-service';
import { AssistantStatus } from './protocol';

const anthropic: AssistantStatus = { enabled: true, provider: 'anthropic', model: 'claude-opus-5-5', reason: null };

function service(): AssistantService {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: Router, useValue: { url: '/', navigate: vi.fn(() => Promise.resolve(true)) } },
      { provide: WorkspaceService, useValue: { currentPage: () => null } },
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
