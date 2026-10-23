import { Component, ElementRef, InjectionToken, effect, inject, input, signal, viewChild } from '@angular/core';
import { renderDiagram } from './mermaid-loader';
import { safeSvg } from './svg-safe';

/** Draws a diagram's source as SVG text; a token so a test can draw without Mermaid, which needs a real layout engine. */
export const DIAGRAM_RENDERER = new InjectionToken<(source: string) => Promise<string>>('DIAGRAM_RENDERER', {
  providedIn: 'root',
  factory: () => renderDiagram,
});

/**
 * One Mermaid diagram (widget-workspace spec §8.3). The drawing passes through `safeSvg` before it joins the page, and
 * a source Mermaid refuses is shown as text beside Mermaid's message, so nothing the operator wrote is hidden.
 */
@Component({
  selector: 'app-mermaid-diagram',
  template: `
    @if (error(); as message) {
      <p class="error" role="alert" i18n>The diagram could not be drawn: {{ message }}</p>
      <pre class="source" tabindex="0">{{ source() }}</pre>
    }
    <div class="drawing" #drawing></div>
  `,
  styles: `
    :host {
      display: block;
    }
    /* Mermaid's neutral theme is drawn for a light page, whatever the console's theme. */
    .drawing {
      background: #fff;
      color: #000;
      border-radius: 0.25rem;
      padding: 0.5rem;
      overflow: auto;
    }
    .drawing:empty {
      display: none;
    }
    .drawing :is(svg) {
      max-width: 100%;
      height: auto;
    }
    .error {
      color: var(--mat-sys-error);
      margin: 0 0 0.5rem;
    }
    .source {
      margin: 0;
      overflow: auto;
    }
  `,
})
export class MermaidDiagram {
  private readonly draw = inject(DIAGRAM_RENDERER);
  private readonly drawing = viewChild.required<ElementRef<HTMLElement>>('drawing');

  readonly source = input.required<string>();
  protected readonly error = signal<string | null>(null);

  /** Bumped by each draw, so a slow one that is overtaken by a newer source does not put its drawing back. */
  private latest = 0;

  constructor() {
    effect(() => {
      const source = this.source();
      const target = this.drawing().nativeElement;
      const mine = ++this.latest;
      this.draw(source).then(
        (svg) => {
          if (mine !== this.latest) return;
          const drawn = safeSvg(svg);
          target.replaceChildren(...(drawn ? [drawn] : []));
          this.error.set(drawn ? null : $localize`Mermaid did not return a drawing.`);
        },
        (e: unknown) => {
          if (mine !== this.latest) return;
          target.replaceChildren();
          this.error.set(e instanceof Error ? e.message : String(e));
        },
      );
    });
  }
}
