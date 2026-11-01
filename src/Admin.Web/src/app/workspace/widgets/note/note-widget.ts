import { Component, computed, inject, input } from '@angular/core';
import { injectWidgetContext } from '../../widget-context';
import { WorkspaceService } from '../../workspace-service';
import { MarkdownBlocks } from './markdown-blocks';

/**
 * A note (widget-workspace spec §8.3): Markdown whose mermaid fences are diagrams, written in place while the layout
 * is being edited. `MarkdownBlocks` draws it as elements, never as HTML, so a tag in the text is text on the page; a
 * diagram passes through Mermaid's strict level and `safeSvg`.
 */
@Component({
  selector: 'app-note-widget',
  imports: [MarkdownBlocks],
  template: `
    @if (editable()) {
      <textarea
        class="source"
        aria-label="Note text"
        i18n-aria-label
        spellcheck="false"
        [attr.maxlength]="maxLength"
        [value]="source()"
        (input)="write($any($event.target).value)"
      ></textarea>
    } @else if (source().trim() === '') {
      <p class="empty" i18n>This note is empty: choose Edit layout to write in it.</p>
    } @else {
      <app-markdown-blocks [source]="source()" />
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .source {
      box-sizing: border-box;
      width: 100%;
      height: 100%;
      min-height: 6rem;
      resize: none;
      font: var(--mat-sys-body-medium);
      font-family: monospace;
    }
    .empty {
      margin: 0;
      color: var(--app-muted);
    }
  `,
})
export class NoteWidget {
  private readonly workspace = inject(WorkspaceService);
  private readonly context = injectWidgetContext();

  /**
   * Characters a note may hold. The host refuses a widget config over `WorkspaceSchema.MaxConfigBytes` (64 KiB of
   * UTF-8, measured on the JSON the host writes, which escapes a non-ASCII character as a six-byte \uXXXX) and the whole
   * workspace travels in one PUT, so the bound is a sixth of that: six bytes a character at worst.
   */
  readonly maxLength = 10000;

  readonly config = input<Record<string, unknown>>({ source: '' });

  readonly source = computed(() => {
    const source = this.config()['source'];
    return typeof source === 'string' ? source : '';
  });

  /** Written in place only while the layout is being edited, and only where there is an instance to save it to. */
  readonly editable = computed(() => this.workspace.editing() && this.context !== null);

  write(source: string): void {
    if (this.context) this.workspace.configureWidget(this.context.pageId, this.context.instanceId, { source });
  }
}
