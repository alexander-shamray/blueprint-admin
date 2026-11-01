import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { Block, blocks } from '../../../features/runbooks/runbook-markdown';
import { RunbookSpans } from '../../../features/runbooks/runbook-spans';
import { MermaidDiagram } from './mermaid-diagram';

/**
 * Markdown drawn as elements, never as HTML, so a tag in the text is text on the page: read by the Runbooks screen's
 * reader, with a mermaid fence drawn as a diagram through Mermaid's strict level and `safeSvg` where `diagrams` allows.
 * A note's text and the assistant's reply are drawn by it, the second as it streams, so the source is parsed again
 * only when it changes.
 */
@Component({
  selector: 'app-markdown-blocks',
  imports: [NgTemplateOutlet, RunbookSpans, MermaidDiagram],
  template: `
    @for (b of parsed(); track $index) {
      @switch (b.kind) {
        @case ('heading') {
          @switch (b.level) {
            @case (1) {
              <h3><app-runbook-spans [spans]="b.spans" [targets]="linkTargets()" /></h3>
            }
            @case (2) {
              <h4><app-runbook-spans [spans]="b.spans" [targets]="linkTargets()" /></h4>
            }
            @default {
              <h5><app-runbook-spans [spans]="b.spans" [targets]="linkTargets()" /></h5>
            }
          }
        }
        @case ('paragraph') {
          <p><app-runbook-spans [spans]="b.spans" [targets]="linkTargets()" /></p>
        }
        @case ('list') {
          <ng-container *ngTemplateOutlet="listItems; context: { $implicit: b.items }" />
        }
        @case ('code') {
          @if (b.lang === 'mermaid' && diagrams()) {
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
                  <th><app-runbook-spans [spans]="h" [targets]="linkTargets()" /></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of b.rows; track $index) {
                <tr>
                  @for (cell of row; track $index) {
                    <td><app-runbook-spans [spans]="cell" [targets]="linkTargets()" /></td>
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

    <ng-template #listItems let-items>
      @if (items[0]?.ordered) {
        <ol [attr.start]="items[0].number ?? null">
          <ng-container *ngTemplateOutlet="listEntries; context: { $implicit: items }" />
        </ol>
      } @else {
        <ul>
          <ng-container *ngTemplateOutlet="listEntries; context: { $implicit: items }" />
        </ul>
      }
    </ng-template>

    <ng-template #listEntries let-items>
      @for (item of items; track $index) {
        <li>
          <app-runbook-spans [spans]="item.spans" [targets]="linkTargets()" />
          @if (item.children.length > 0) {
            <ng-container *ngTemplateOutlet="listItems; context: { $implicit: item.children }" />
          }
        </li>
      }
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
    }
    h3,
    h4,
    h5 {
      margin: 0.5rem 0 0.25rem;
    }
    p {
      margin: 0 0 0.5rem;
    }
    ul,
    ol {
      margin: 0 0 0.5rem;
      padding-left: 1.25rem;
    }
    li > :is(ul, ol) {
      margin-bottom: 0;
    }
    .code {
      overflow: auto;
      margin: 0 0 0.5rem;
    }
    table.md {
      border-collapse: collapse;
      margin-bottom: 0.5rem;
    }
    table.md :is(th, td) {
      border: 1px solid var(--app-border);
      padding: 0.25rem 0.5rem;
      text-align: left;
    }
    :host > :last-child {
      margin-bottom: 0;
    }
  `,
})
export class MarkdownBlocks {
  readonly source = input.required<string>();
  /**
   * Whether a mermaid fence is drawn, or shown as its source. Off for text the operator did not write: a diagram's
   * `%%{init}%%` directive can carry `themeCSS`, which strict mode does not refuse and `safeSvg` does not scope, so a
   * steered model could restyle the console around it.
   */
  readonly diagrams = input(true);
  /**
   * Whether a link's target is shown after its words, as text. Off for a note, as for a runbook, whose targets are
   * paths in the backend clone; on for the assistant's reply, where a URL it cites is the answer.
   */
  readonly linkTargets = input(false);
  readonly parsed = computed<Block[]>(() => blocks(this.source()));
}
