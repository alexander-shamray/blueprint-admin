import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SCREENS } from '../screens';
import { builtInWidgets, defaultWorkspace } from './built-in-pages';
import { SAVE_DELAY_MS, WorkspaceService } from './workspace-service';
import { Workspace } from './workspace-types';

describe('WorkspaceService', () => {
  let service: WorkspaceService;
  let http: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(WorkspaceService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  function load(workspace: Workspace = defaultWorkspace(), etag = '"e1"'): void {
    service.load();
    http.expectOne('/api/workspace').flush(workspace, { headers: { ETag: etag } });
  }

  function expectSave(etag: string) {
    const req = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/workspace');
    expect(req.request.headers.get('If-Match')).toBe(etag);
    return req;
  }

  it('reads the workspace and the etag a save must name', () => {
    load();
    expect(service.loaded()).toBe(true);
    expect(service.pages().map((p) => p.id)).toEqual(SCREENS.map((s) => s.path));
  });

  it('adds a built-in page a saved workspace lacks, so the defaults stay one click away', () => {
    const saved = defaultWorkspace();
    saved.pages = saved.pages.filter((p) => p.builtIn !== 'mail');
    load(saved);
    expect(service.pages().at(-1)?.builtIn).toBe('mail');
  });

  it('saves once, about a second after the last of several changes', () => {
    load();
    service.renamePage('stack', 'One');
    vi.advanceTimersByTime(SAVE_DELAY_MS / 2);
    service.renamePage('stack', 'Two');
    vi.advanceTimersByTime(SAVE_DELAY_MS - 1);
    http.expectNone((r) => r.method === 'PUT');
    expect(service.saveState().kind).toBe('pending');

    vi.advanceTimersByTime(1);
    const req = expectSave('"e1"');
    expect((req.request.body as Workspace).pages[0].title).toBe('Two');
    req.flush(null, { headers: { ETag: '"e2"' } });
    expect(service.saveState().kind).toBe('saved');

    service.renamePage('stack', 'Three');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    expectSave('"e2"').flush(null, { headers: { ETag: '"e3"' } });
  });

  it('never saves while a widget is being dragged, and saves what the drag left', () => {
    load();
    service.setDragging(true);
    service.setWidgets('stack', builtInWidgets('stack').map((w) => ({ ...w, cols: 12 })));
    vi.advanceTimersByTime(SAVE_DELAY_MS * 5);
    http.expectNone((r) => r.method === 'PUT');

    service.setDragging(false);
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    const req = expectSave('"e1"');
    expect((req.request.body as Workspace).pages[0].widgets?.[0].cols).toBe(12);
    req.flush(null, { headers: { ETag: '"e2"' } });
  });

  it('saves again when a change lands while a save is in flight', () => {
    load();
    service.renamePage('stack', 'One');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    const first = expectSave('"e1"');
    service.renamePage('stack', 'Two');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    http.expectNone((r) => r.method === 'PUT');

    first.flush(null, { headers: { ETag: '"e2"' } });
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    const second = expectSave('"e2"');
    expect((second.request.body as Workspace).pages[0].title).toBe('Two');
    second.flush(null, { headers: { ETag: '"e3"' } });
  });

  it('says a failed save, and retries on request', () => {
    load();
    service.renamePage('stack', 'One');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    expectSave('"e1"').flush({ detail: 'disk full' }, { status: 500, statusText: 'Server Error' });
    expect(service.saveState()).toEqual({ kind: 'failed', message: 'disk full' });

    service.retry();
    vi.advanceTimersByTime(0);
    expectSave('"e1"').flush(null, { headers: { ETag: '"e2"' } });
    expect(service.saveState().kind).toBe('saved');
  });

  it('holds a 412 until the operator takes the saved workspace', () => {
    load();
    service.renamePage('stack', 'Mine');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    const theirs = defaultWorkspace();
    theirs.pages[0].title = 'Theirs';
    expectSave('"e1"').flush(theirs, { status: 412, statusText: 'Precondition Failed', headers: { ETag: '"e9"' } });
    expect(service.saveState().kind).toBe('conflict');

    service.renamePage('stack', 'Later');
    vi.advanceTimersByTime(SAVE_DELAY_MS * 3);
    http.expectNone((r) => r.method === 'PUT');

    service.resolveConflict('theirs');
    expect(service.pages()[0].title).toBe('Theirs');
    expect(service.saveState().kind).toBe('saved');
    service.renamePage('stack', 'Next');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    expectSave('"e9"').flush(null, { headers: { ETag: '"e10"' } });
  });

  it('saves this tab over a 412 when the operator keeps it', () => {
    load();
    service.renamePage('stack', 'Mine');
    vi.advanceTimersByTime(SAVE_DELAY_MS);
    expectSave('"e1"').flush(defaultWorkspace(), { status: 412, statusText: 'Precondition Failed', headers: { ETag: '"e9"' } });

    service.resolveConflict('mine');
    vi.advanceTimersByTime(0);
    const req = expectSave('"e9"');
    expect((req.request.body as Workspace).pages[0].title).toBe('Mine');
    req.flush(null, { headers: { ETag: '"e10"' } });
    expect(service.saveState().kind).toBe('saved');
  });

  it('sends nothing when the workspace could not be read', () => {
    service.load();
    http.expectOne('/api/workspace').flush({ detail: 'locked' }, { status: 500, statusText: 'Server Error' });
    expect(service.saveState()).toEqual({ kind: 'unavailable', message: 'locked' });
    expect(service.pages().length).toBe(SCREENS.length);

    service.renamePage('stack', 'One');
    vi.advanceTimersByTime(SAVE_DELAY_MS * 3);
    http.expectNone((r) => r.method === 'PUT');
  });

  it('opening a page is not an edit', () => {
    load();
    service.open('logs');
    vi.advanceTimersByTime(SAVE_DELAY_MS * 3);
    http.expectNone((r) => r.method === 'PUT');
    expect(service.workspace().activePageId).toBe('logs');
  });

  describe('pages', () => {
    beforeEach(() => load());
    afterEach(() => {
      vi.advanceTimersByTime(SAVE_DELAY_MS);
      for (const req of http.match((r) => r.method === 'PUT')) req.flush(null, { headers: { ETag: '"x"' } });
    });

    it('adds an empty page of the operator’s own', () => {
      const id = service.addPage();
      expect(service.pages().at(-1)).toEqual({ id, title: 'New page', builtIn: null, hidden: false, widgets: [] });
      expect(service.pageLink(id)).toEqual(['/page', id]);
      expect(service.pageLink('trace')).toEqual(['/', 'trace']);
    });

    it('opens a built-in page a newer console added by its id, since no route here knows its screen', () => {
      const newer = defaultWorkspace();
      newer.pages.push({ id: 'graphs', title: 'Graphs', builtIn: 'graphs', hidden: false, widgets: null });
      service.workspace.set(newer);
      expect(service.pageLink('graphs')).toEqual(['/page', 'graphs']);
    });

    it('renames, ignoring a blank title', () => {
      service.renamePage('logs', '  Tail  ');
      service.renamePage('logs', '   ');
      expect(service.pages()[1].title).toBe('Tail');
    });

    it('moves a page among the visible ones and to a named place', () => {
      service.movePageBy('stack', 1);
      expect(service.pages().slice(0, 2).map((p) => p.id)).toEqual(['logs', 'stack']);
      service.setHidden('stack', true);
      service.movePageBy('logs', 1);
      expect(service.visiblePages().slice(0, 2).map((p) => p.id)).toEqual(['broker', 'logs']);
      service.movePage('transcript', 'broker');
      expect(service.visiblePages()[0].id).toBe('transcript');
    });

    it('hides a built-in page rather than deleting it, and deletes the operator’s', () => {
      service.deletePage('mail');
      expect(service.hiddenPages().map((p) => p.id)).toEqual(['mail']);
      const id = service.addPage();
      service.deletePage(id);
      expect(service.pages().some((p) => p.id === id)).toBe(false);
    });

    it('puts a deleted page back where it stood', () => {
      const id = service.addPage();
      service.movePage(id, 'logs');
      const page = service.pages()[1];
      service.deletePage(id);
      service.restorePage(page, 1);
      expect(service.pages()[1]).toEqual(page);
    });

    it('duplicates a built-in page as the operator’s own, every widget with a new id', () => {
      const id = service.duplicatePage('trace');
      const copy = service.pages().find((p) => p.id === id);
      expect(copy?.title).toBe('Trace (copy)');
      expect(copy?.builtIn).toBeNull();
      const [widget] = copy?.widgets ?? [];
      expect(widget.config).toEqual({ screen: 'trace' });
      expect(widget.id).not.toBe(builtInWidgets('trace')[0].id);
    });

    it('resets a built-in page to its shipped widgets and title', () => {
      service.renamePage('logs', 'Tail');
      service.setWidgets('logs', []);
      service.resetPage('logs');
      expect(service.pages()[1]).toEqual({ id: 'logs', title: 'Logs', builtIn: 'logs', hidden: false, widgets: null });
    });

    it('adds a widget below the others and removes it', () => {
      service.addWidget('stack', { type: 'legacy', cols: 6, rows: 4, config: { screen: 'logs' } });
      const widgets = service.pages()[0].widgets ?? [];
      expect(widgets.length).toBe(2);
      expect(widgets[1]).toMatchObject({ x: 0, y: builtInWidgets('stack')[0].rows, cols: 6, rows: 4 });
      service.removeWidget('stack', widgets[1].id);
      expect(service.pages()[0].widgets?.length).toBe(1);
    });
  });
});
