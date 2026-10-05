import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScenarioLauncher } from '../../features/scenario/scenario-launcher';
import { SCREENS } from '../../screens';
import { CommandPalette } from './command-palette';

describe('CommandPalette', () => {
  // jsdom has no modal dialog; these stand in for the browser's, which Playwright drives for real.
  beforeAll(() => {
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
      this.removeAttribute('open');
    };
  });

  let navigate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [CommandPalette], providers: [provideRouter([])] });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true) as unknown as ReturnType<typeof vi.fn>;
  });

  function render() {
    const fixture = TestBed.createComponent(CommandPalette);
    fixture.detectChanges();
    return fixture;
  }

  function key(target: EventTarget, init: KeyboardEventInit): void {
    target.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }));
  }

  function type(fixture: ReturnType<typeof render>, text: string): HTMLInputElement {
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    return input;
  }

  it('opens on Ctrl+K and on Cmd+K, with the input focused, and not on Ctrl+Alt+K', () => {
    const fixture = render();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;

    key(document, { key: 'k', ctrlKey: true, altKey: true });
    expect(dialog.open).toBe(false);

    key(document, { key: 'k', ctrlKey: true });
    expect(dialog.open).toBe(true);
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('input'));

    dialog.close();
    key(document, { key: 'K', metaKey: true });
    expect(dialog.open).toBe(true);
  });

  it('offers every screen the bar shows, then the Scenario run', () => {
    const palette = render().componentInstance;

    expect(palette.commands().map((c) => c.label)).toEqual([
      ...SCREENS.map((s) => `Go to ${s.label}`),
      'Run the Scenario',
    ]);
  });

  it('filters by what was typed and runs the chosen command on Enter', () => {
    const fixture = render();
    fixture.componentInstance.open();

    const input = type(fixture, 'brok');
    key(input, { key: 'Enter' });

    expect(navigate).toHaveBeenCalledWith(['/', 'broker']);
    expect((fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).open).toBe(false);
  });

  it('offers what was typed as a correlation id, reached by arrow keys, and leaves judging it to the trace endpoint', () => {
    const fixture = render();
    fixture.componentInstance.open();

    const input = type(fixture, 'demo-trace-0001');
    expect(fixture.componentInstance.commands().map((c) => c.label)).toEqual(['Open trace for demo-trace-0001']);
    key(input, { key: 'Enter' });

    expect(navigate).toHaveBeenCalledWith(['/trace', 'demo-trace-0001']);
  });

  it('moves the active option with the arrow keys, wrapping at both ends, and names it for assistive technology', () => {
    const fixture = render();
    const palette = fixture.componentInstance;
    palette.open();
    const input = type(fixture, 'go to');

    // Every screen matches, and the typed text is offered last as a correlation id.
    key(input, { key: 'ArrowUp' });
    fixture.detectChanges();
    expect(palette.active()).toBe(SCREENS.length);
    expect(input.getAttribute('aria-activedescendant')).toBe('palette-trace');

    key(input, { key: 'ArrowDown' });
    expect(palette.active()).toBe(0);
  });

  it('asks the Scenario screen for a run and goes there, rather than putting the run in the URL', () => {
    const fixture = render();
    const launcher = TestBed.inject(ScenarioLauncher);
    fixture.componentInstance.open();

    const input = type(fixture, 'run');
    key(input, { key: 'Enter' });

    expect(launcher.pending()).toBe(true);
    expect(navigate).toHaveBeenCalledWith(['/scenario']);
  });
});
