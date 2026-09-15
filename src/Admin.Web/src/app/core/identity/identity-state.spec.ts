import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../host/host-client';
import { IdentityState } from './identity-state';

describe('IdentityState', () => {
  function setup(users = of([{ username: 'demo' }, { username: 'browser' }])): IdentityState {
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: { identityUsers: () => users } }] });
    return TestBed.inject(IdentityState);
  }

  it('starts anonymous and sends no identity', () => {
    const state = setup();

    expect(state.choice()).toEqual({ kind: 'anonymous' });
    expect(state.request()).toBeNull();
    expect(state.label()).toBe('anonymous');
  });

  it('loads the realm users and selects demo when it exists', () => {
    const state = setup();
    state.load();

    expect(state.users().map((u) => u.username)).toEqual(['demo', 'browser']);
    expect(state.choice()).toEqual({ kind: 'user', username: 'demo' });
    expect(state.request()).toEqual({ username: 'demo', password: null });
  });

  it('keeps an explicit choice when users load later', () => {
    const state = setup();
    state.select({ kind: 'user', username: 'browser' });
    state.load();

    expect(state.choice()).toEqual({ kind: 'user', username: 'browser' });
  });

  it('sends a custom identity with its password and labels it without the password', () => {
    const state = setup();
    state.select({ kind: 'custom', username: 'alice', password: 's3cret' });

    expect(state.request()).toEqual({ username: 'alice', password: 's3cret' });
    expect(state.label()).toBe('alice (custom)');
  });

  it('stays usable when the users request fails', () => {
    const state = setup(throwError(() => new Error('offline')));
    state.load();

    expect(state.users()).toEqual([]);
    expect(state.choice()).toEqual({ kind: 'anonymous' });
  });
});
