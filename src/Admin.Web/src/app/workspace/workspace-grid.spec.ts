import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { GridsterItem } from 'angular-gridster2';
import { WIDGET_CONTEXT } from './widget-context';
import { JOB_OUTPUT, NOTE } from './widgets';
import { WorkspaceGrid } from './workspace-grid';
import { GRID_COLUMNS, MAX_ROWS, WidgetInstance } from './workspace-types';

const known: WidgetInstance = {
  id: 'w1',
  type: 'alerts',
  x: 0,
  y: 0,
  cols: 12,
  rows: 6,
  config: { firingOnly: true },
};
const unknown: WidgetInstance = {
  id: 'w2',
  type: 'from-a-newer-console',
  x: 12,
  y: 0,
  cols: 6,
  rows: 4,
  config: { a: 1 },
};

describe('WorkspaceGrid', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }),
  );

  function render(widgets: WidgetInstance[], editing: boolean) {
    const fixture = TestBed.createComponent(WorkspaceGrid);
    fixture.componentRef.setInput('pageId', 'p1');
    fixture.componentRef.setInput('widgets', widgets);
    fixture.componentRef.setInput('editing', editing);
    fixture.detectChanges();
    return fixture;
  }

  it('frames each widget under the title its type gives it, and keeps an unknown type', () => {
    const element: HTMLElement = render([known, unknown], false).nativeElement;
    const titles = [...element.querySelectorAll('.ws-head .ws-title')].map((h) => h.textContent?.trim());
    expect(titles).toEqual(['Alerts (firing)', 'Unknown widget (from-a-newer-console)']);
    expect(element.querySelector('.ws-unknown')?.textContent).toContain('from-a-newer-console');
  });

  it('tells each widget its instance and page, and keeps that injector while the layout moves', () => {
    const fixture = render([known, unknown], false);
    const grid = fixture.componentInstance;
    const injector = grid.injectorOf(grid.items()[0]);
    expect(injector.get(WIDGET_CONTEXT)).toEqual({ instanceId: 'w1', pageId: 'p1' });

    fixture.componentRef.setInput('widgets', [{ ...known, x: 6 }, unknown]);
    fixture.detectChanges();
    expect(grid.injectorOf(grid.items()[0])).toBe(injector);
    expect(grid.injectorOf(grid.items()[1]).get(WIDGET_CONTEXT).instanceId).toBe('w2');
  });

  it('shows the remove control only while editing, and reports which widget', () => {
    expect(render([known], false).nativeElement.querySelector('button.ws-remove')).toBeNull();

    const fixture = render([known], true);
    let removed: string | undefined;
    fixture.componentInstance.removeWidget.subscribe((id) => (removed = id));
    (fixture.nativeElement.querySelector('button.ws-remove') as HTMLButtonElement).click();
    expect(removed).toBe('w1');
  });

  it('offers a type’s presets as its configure control while editing, marks the current one, and reports the choice', () => {
    expect(render([known], false).nativeElement.querySelector('button.ws-configure')).toBeNull();

    const fixture = render([known], true);
    let chosen: { id: string; config: Record<string, unknown> } | undefined;
    fixture.componentInstance.configureWidget.subscribe((c) => (chosen = c));
    (fixture.nativeElement.querySelector('button.ws-configure') as HTMLButtonElement).click();
    fixture.detectChanges();

    const items = [...document.querySelectorAll<HTMLElement>('.mat-mdc-menu-item')];
    expect(items.length).toBe(2);
    expect(items.filter((i) => i.getAttribute('aria-current') === 'true').map((i) => i.textContent?.trim())).toEqual([
      'Firing only',
    ]);
    items.find((i) => i.textContent?.trim() === 'Every rule')?.click();
    expect(chosen).toEqual({ id: 'w1', config: { firingOnly: false } });
  });

  it('edits a setting no preset holds: a list of names, written with commas, saved over what the widget held', async () => {
    const services: WidgetInstance = {
      id: 's',
      type: 'compose-services',
      x: 0,
      y: 0,
      cols: 12,
      rows: 6,
      config: { services: ['a'] },
    };
    const fixture = render([services], true);
    const element: HTMLElement = fixture.nativeElement;
    let chosen: { id: string; config: Record<string, unknown> } | undefined;
    fixture.componentInstance.configureWidget.subscribe((c) => (chosen = c));

    (element.querySelector('button.ws-configure') as HTMLButtonElement).click();
    fixture.detectChanges();
    const items = [...document.querySelectorAll<HTMLElement>('.mat-mdc-menu-item')];
    expect(items.map((i) => i.textContent?.trim())).toContain('Edit settings…');
    document.querySelector<HTMLElement>('.ws-edit-settings')?.click();
    fixture.detectChanges();

    const input = element.querySelector<HTMLInputElement>('.ws-settings input')!;
    expect(input.value).toBe('a');
    input.value = ' x, y ,, ';
    input.dispatchEvent(new Event('input'));
    element.querySelector('.ws-settings')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    expect(chosen).toEqual({ id: 's', config: { services: ['x', 'y'] } });
    expect(element.querySelector('.ws-settings')).toBeNull();
    await fixture.whenStable();
  });

  it('drops an open draft when a preset is chosen, the widget is removed, or the page changes', () => {
    const job: WidgetInstance = { id: 'j', type: 'job-output', x: 0, y: 0, cols: 12, rows: 6, config: { jobId: 'x' } };
    const fixture = render([job], true);
    const open = (): void => {
      (fixture.nativeElement.querySelector('button.ws-configure') as HTMLButtonElement).click();
      fixture.detectChanges();
      document.querySelector<HTMLElement>('.ws-edit-settings')?.click();
      fixture.detectChanges();
    };
    const panel = (): Element | null => fixture.nativeElement.querySelector('.ws-settings');

    open();
    expect(panel()).not.toBeNull();
    fixture.componentInstance.choosePreset(fixture.componentInstance.items()[0], JOB_OUTPUT.presets[0]);
    fixture.detectChanges();
    expect(panel()).toBeNull();

    open();
    fixture.componentRef.setInput('widgets', []);
    fixture.detectChanges();
    fixture.componentRef.setInput('widgets', [job]);
    fixture.detectChanges();
    expect(panel()).toBeNull();

    open();
    fixture.componentRef.setInput('pageId', 'p2');
    fixture.detectChanges();
    expect(panel()).toBeNull();
  });

  it('closes an open settings panel when the layout editor is left', () => {
    const job: WidgetInstance = { id: 'j', type: 'job-output', x: 0, y: 0, cols: 12, rows: 6, config: { jobId: null } };
    const fixture = render([job], true);
    (fixture.nativeElement.querySelector('button.ws-configure') as HTMLButtonElement).click();
    fixture.detectChanges();
    document.querySelector<HTMLElement>('.ws-edit-settings')?.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.ws-settings')).not.toBeNull();
    // A job id is not a list of names, so the comma hint is not shown for it.
    expect(fixture.nativeElement.querySelector('.ws-field-hint')).toBeNull();

    fixture.componentRef.setInput('editing', false);
    fixture.detectChanges();
    fixture.componentRef.setInput('editing', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.ws-settings')).toBeNull();
  });

  it('has no configure control for a type with nothing to choose, or one this console does not know', () => {
    const plain: WidgetInstance = { id: 'p', type: 'stack-controls', x: 0, y: 0, cols: 12, rows: 6, config: {} };
    expect(render([plain, unknown], true).nativeElement.querySelector('button.ws-configure')).toBeNull();
  });

  it('does not offer a note its presets, which would replace what was written', async () => {
    const note: WidgetInstance = { id: 'n', type: 'note', x: 0, y: 0, cols: 12, rows: 6, config: { source: '# Mine' } };
    const fixture = render([note], true);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('button.ws-configure')).toBeNull();
    expect(element.querySelector('.ws-title')?.textContent).toBe('Mine');
    // The note's lazy component is loading; let it land before the test ends.
    await NOTE.component();
    await fixture.whenStable();
  });

  it('maximises one widget across the page and restores the grid, keeping the layout', () => {
    const fixture = render([known, { ...known, id: 'w3', x: 12 }], false);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('gridster-item').length).toBe(2);

    (element.querySelectorAll('button.ws-maximise')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelectorAll('gridster-item').length).toBe(0);
    expect(element.querySelectorAll('.ws-maximised').length).toBe(1);
    expect(element.querySelector('.ws-maximised')?.getAttribute('data-widget-id')).toBe('w3');
    const restore = element.querySelector('button.ws-maximise') as HTMLButtonElement;
    expect(restore.getAttribute('aria-pressed')).toBe('true');

    restore.click();
    fixture.detectChanges();
    expect(element.querySelectorAll('gridster-item').length).toBe(2);
    expect(element.querySelector('.ws-maximised')).toBeNull();
  });

  it('ends a maximise when the widget is removed', () => {
    const fixture = render([known, { ...known, id: 'w3', x: 12 }], true);
    (fixture.nativeElement.querySelectorAll('button.ws-maximise')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.componentRef.setInput('widgets', [known]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.ws-maximised')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('gridster-item').length).toBe(1);
  });

  it('places a full-width widget far below gridster’s own hundred rows, where it was saved', async () => {
    // The first widget fills gridster's default hundred rows, so without the grid's own row limit the second has
    // nowhere to go and is left `notPlaced`, which is what hides an item. The `display` gridster writes from that is
    // set only in a layout pass, which jsdom, measuring nothing, never runs.
    const wide = { ...known, cols: GRID_COLUMNS, rows: MAX_ROWS };
    const low = { ...known, id: 'low', cols: GRID_COLUMNS, y: 7 * 16 };
    const fixture = render([wide, low], false);
    await fixture.whenStable();
    fixture.detectChanges();
    const placed = fixture.debugElement
      .queryAll(By.directive(GridsterItem))
      .map((d) => !(d.componentInstance as GridsterItem).notPlaced);
    expect(placed).toEqual([true, true]);
    expect(fixture.componentInstance.items()[1].y).toBe(7 * 16);
  });

  it('locks the layout unless editing', () => {
    const grid = render([known], false).componentInstance;
    expect(grid.options().draggable?.enabled).toBe(false);
    expect(grid.options().resizable?.enabled).toBe(false);

    const editing = render([known], true).componentInstance;
    expect(editing.options().draggable?.enabled).toBe(true);
    expect(editing.options().resizable?.enabled).toBe(true);
  });

  it('reports a moved widget as the page’s whole layout, the unknown one untouched', () => {
    const fixture = render([known, unknown], true);
    let layout: WidgetInstance[] | undefined;
    fixture.componentInstance.layoutChange.subscribe((l) => (layout = l));
    const [first] = fixture.componentInstance.items();
    first.x = 6;
    first.cols = 10;
    fixture.componentInstance.options().itemChangeCallback?.(first, undefined as never);
    expect(layout).toEqual([{ ...known, x: 6, cols: 10 }, unknown]);
  });
});
