import { Component, input } from '@angular/core';
import { Span } from './runbook-markdown';

/** One line of a runbook: its plain, `code` and **strong** runs as elements, with no HTML ever built from the text. */
@Component({
  selector: 'app-runbook-spans',
  template: `@for (s of spans(); track $index) {@switch (s.kind) {@case ('code') {<code>{{ s.text }}</code>}@case ('strong') {<strong>{{ s.text }}</strong>}@default {{{ s.text }}}}}`,
})
export class RunbookSpans {
  readonly spans = input.required<Span[]>();
}
