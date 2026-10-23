import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { Block, blocks } from '../../../features/runbooks/runbook-markdown';
import { RunbookSpans } from '../../../features/runbooks/runbook-spans';
import { injectWidgetContext } from '../../widget-context';
import { WorkspaceService } from '../../workspace-service';
import { MermaidDiagram } from './mermaid-diagram';

/**
 * A note (widget-workspace spec §8.3): Markdown whose mermaid fences are diagrams, written in place while the layout
 * is being edited. The Markdown is read by the Runbooks screen's reader, and drawn as elements, never as HTML, so a tag
 * in the text is text on the page; a diagram passes through Mermaid's strict level and `safeSvg`.
 */
@Component({
  selector: 'app-note-widget',
  imports: [NgTemplateOutlet, RunbookSpans, MermaidDiagram],
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
    } @else if (parsed().length === 0) {
      <p class="empty" i18n>This note is empty: choose Edit layout to write in it.</p>
    } @else {
      @for (b of parsed(); track $index) {
        @switch (b.kind) {
          @case ('heading') {
            @switch (b.level) {
              @case (1) {<h3><app-runbook-spans [spans]="b.spans" /></h3>}
              @case (2) {<h4><app-runbook-spans [spans]="b.spans" /></h4>}
              @default {<h5><app-runbook-spans [spans]="b.spans" /></h5>}
            }
          }
          @case ('paragraph') {
            <p><app-runbook-spans [spans]="b.spans" /></p>
          }
          @case ('list') {
            <ng-container *ngTemplateOutlet="listItems; context: { $implicit: b.items }" />
          }
          @case ('code') {
            @if (b.lang === 'mermaid') {
              <app-mermaid-diagram [source]="b.text" />
            } @else {
              <pre class="code" tabindex="0">{{ b.text }}</pre>
            }
          }
          @case ('table') {
            <table class="md">
              <thead>
                <tr>
                  @for (h of b.header; track $index) {
                    <th><app-runbook-spans [spans]="h" /></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of b.rows; track $index) {
                  <tr>
                    @for (cell of row; track $index) {
                      <td><app-runbook-spans [spans]="cell" /></td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          }
          @case ('rule') {
            <hr />
          }
        }
      }
    }

    <ng-template #listItems let-items>
      <ul>
        @for (item of items; track $index) {
          <li>
            <app-runbook-spans [spans]="item.spans" />
            @if (item.children.length > 0) {
              <ng-container *ngTemplateOutlet="listItems; context: { $implicit: item.children }" />
            }
          </li>
        }
      </ul>
    </ng-template>
  `,
  styles: `
    :host { display: block; height: 100%; }
    .source { box-sizing: border-box; width: 100%; height: 100%; min-height: 6rem; resize: none; font: var(--mat-sys-body-medium); font-family: monospace; }
    .empty { margin: 0; color: var(--app-muted); }
    h3, h4, h5 { margin: 0.5rem 0 0.25rem; }
    p { margin: 0 0 0.5rem; }
    .code { overflow: auto; margin: 0 0 0.5rem; }
    table.md { border-collapse: collapse; margin-bottom: 0.5rem; }
    table.md :is(th, td) { border: 1px solid var(--app-border); padding: 0.25rem 0.5rem; text-align: left; }
  `,
})
export class NoteWidget {
  private readonly workspace = inject(WorkspaceService);
  private readonly context = injectWidgetContext();

  /**
   * Characters a note may hold. The host refuses a widget config over `WorkspaceSchema.MaxConfigBytes` (64 KiB of
   * UTF-8) and the whole workspace travels in one PUT, so the bound is a quarter of that: four bytes a character at worst.
   */
  readonly maxLength = 16000;

  readonly config = input<Record<string, unknown>>({ source: '' });

  readonly source = computed(() => {
    const source = this.config()['source'];
    return typeof source === 'string' ? source : '';
  });
  readonly parsed = computed<Block[]>(() => blocks(this.source()));

  /** Written in place only while the layout is being edited, and only where there is an instance to save it to. */
  readonly editable = computed(() => this.workspace.editing() && this.context !== null);

  write(source: string): void {
    if (this.context) this.workspace.configureWidget(this.context.pageId, this.context.instanceId, { source });
  }
}
