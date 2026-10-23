import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WIDGET_CONTEXT } from '../../widget-context';
import { WorkspaceService } from '../../workspace-service';
import { DIAGRAM_RENDERER } from './mermaid-diagram';
import { MERMAID_CONFIG } from './mermaid-loader';
import { NoteWidget } from './note-widget';

const FENCE = '```';
const HOSTILE = [
  '# Title <img src=x onerror=alert(1)>',
  '',
  'Text <script>alert(2)</script> and <b onclick="alert(3)">bold</b>',
  '',
  '- item <a href="javascript:alert(4)">link</a>',
  '',
  `${FENCE}mermaid`,
  'flowchart LR',
  '  a["<script>alert(5)</script>"] --> b["<img src=x onerror=alert(6)>"]',
  FENCE,
].join('\n');

describe('NoteWidget', () => {
  let draw: ReturnType<typeof vi.fn<(source: string) => Promise<string>>>;

  beforeEach(() => {
    // What a Mermaid fault might return for the hostile fence: a drawing with script and handlers in it.
    draw = vi.fn(
      async () =>
        '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(7)</script><g onclick="alert(8)"><text>ok</text></g></svg>',
    );
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIAGRAM_RENDERER, useValue: draw },
        { provide: WIDGET_CONTEXT, useValue: { instanceId: 'n1', pageId: 'p1' } },
      ],
    });
  });

  async function render(source: string) {
    const fixture = TestBed.createComponent(NoteWidget);
    fixture.componentRef.setInput('config', { source });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('never lets a script or an event handler in the source reach the DOM', async () => {
    const element: HTMLElement = (await render(HOSTILE)).nativeElement;

    expect(element.querySelector('script')).toBeNull();
    for (const node of element.querySelectorAll('*')) {
      expect([...node.attributes].map((a) => a.name).filter((n) => n.startsWith('on'))).toEqual([]);
      expect(node.getAttribute('href') ?? '').not.toMatch(/javascript:/i);
    }
    // The text stays, as text.
    expect(element.textContent).toContain('<script>alert(2)</script>');
    expect(element.querySelector('h3')?.textContent).toBe('Title <img src=x onerror=alert(1)>');
    // The drawing the stub returned kept its text and lost its script and handler.
    expect(element.querySelector('svg text')?.textContent).toBe('ok');
  });

  it('draws a mermaid fence as a diagram and any other fence as code', async () => {
    const element: HTMLElement = (
      await render(['text', '', `${FENCE}mermaid`, 'flowchart LR', FENCE, '', `${FENCE}bash`, 'ls', FENCE].join('\n'))
    ).nativeElement;

    expect(draw).toHaveBeenCalledOnce();
    expect(draw).toHaveBeenCalledWith('flowchart LR');
    expect(element.querySelectorAll('app-mermaid-diagram').length).toBe(1);
    expect(element.querySelector('pre.code')?.textContent).toBe('ls');
  });

  it('shows the source and Mermaid’s message when a diagram cannot be drawn', async () => {
    draw.mockRejectedValue(new Error('Parse error on line 2'));
    const element: HTMLElement = (await render([`${FENCE}mermaid`, 'not a diagram', FENCE].join('\n'))).nativeElement;

    expect(element.querySelector('[role=alert]')?.textContent).toContain('Parse error on line 2');
    expect(element.querySelector('pre.source')?.textContent).toBe('not a diagram');
  });

  it('says so when empty, and is a textarea while the layout is being edited, saving what is typed', async () => {
    const fixture = await render('');
    expect(fixture.nativeElement.textContent).toContain('This note is empty');

    const workspace = TestBed.inject(WorkspaceService);
    const configure = vi.spyOn(workspace, 'configureWidget').mockImplementation(() => undefined);
    workspace.editing.set(true);
    fixture.detectChanges();
    const area = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    area.value = '# Hello';
    area.dispatchEvent(new Event('input'));
    expect(configure).toHaveBeenCalledWith('p1', 'n1', { source: '# Hello' });
  });

  it('runs Mermaid strict, with no HTML in a label', () => {
    expect(MERMAID_CONFIG.securityLevel).toBe('strict');
    expect(MERMAID_CONFIG.htmlLabels).toBe(false);
    expect(MERMAID_CONFIG.flowchart?.htmlLabels).toBe(false);
  });
});
