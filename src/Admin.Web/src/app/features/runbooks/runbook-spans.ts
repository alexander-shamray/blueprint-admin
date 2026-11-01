import { Component, input } from '@angular/core';
import { Span } from './runbook-markdown';

/**
 * One line of a runbook: its plain, `code` and **strong** runs as elements, with no HTML ever built from the text. A
 * link is its words; with `targets`, its target follows them in brackets as text, never as a link.
 */
@Component({
  selector: 'app-runbook-spans',
  // The runs sit side by side in a line of text, so a space Prettier added inside a block would show.
  // prettier-ignore
  template: `@for (s of spans(); track $index) {@switch (s.kind) {@case ('code') {<code>{{ s.text }}</code>}@case ('strong') {<strong>{{ s.text }}</strong>}@default {{{ s.text }}@if (targets() && s.target) { ({{ s.target }})}}}}`,
})
export class RunbookSpans {
  readonly spans = input.required<Span[]>();
  /** Whether a link's target is shown after its words, for text whose links point somewhere the reader can go. */
  readonly targets = input(false);
}
