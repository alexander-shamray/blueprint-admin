import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { WorkspaceGrid } from './workspace-grid';
import { WidgetInstance } from './workspace-types';

const known: WidgetInstance = { id: 'w1', type: 'legacy', x: 0, y: 0, cols: 12, rows: 6, config: { screen: 'requests' } };
const unknown: WidgetInstance = { id: 'w2', type: 'from-a-newer-console', x: 12, y: 0, cols: 6, rows: 4, config: { a: 1 } };

describe('WorkspaceGrid', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }),
  );

  function render(widgets: WidgetInstance[], editing: boolean) {
    const fixture = TestBed.createComponent(WorkspaceGrid);
    fixture.componentRef.setInput('widgets', widgets);
    fixture.componentRef.setInput('editing', editing);
    fixture.detectChanges();
    return fixture;
  }

  it('frames each widget under the title its type gives it, and keeps an unknown type', () => {
    const element: HTMLElement = render([known, unknown], false).nativeElement;
    const titles = [...element.querySelectorAll('.head .title')].map((h) => h.textContent?.trim());
    expect(titles).toEqual(['API', 'Unknown widget (from-a-newer-console)']);
    expect(element.querySelector('.unknown')?.textContent).toContain('from-a-newer-console');
  });

  it('shows the remove control only while editing, and reports which widget', () => {
    expect(render([known], false).nativeElement.querySelector('button.remove')).toBeNull();

    const fixture = render([known], true);
    let removed: string | undefined;
    fixture.componentInstance.removeWidget.subscribe((id) => (removed = id));
    (fixture.nativeElement.querySelector('button.remove') as HTMLButtonElement).click();
    expect(removed).toBe('w1');
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
