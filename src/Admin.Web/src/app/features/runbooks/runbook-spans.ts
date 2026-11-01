import { Component, computed, inject, input } from '@angular/core';
import { Router, RouterLink, UrlTree } from '@angular/router';
import { Span } from './runbook-markdown';

/** A span as drawn: its route when it is a link to one of the console's own pages. */
type Run = Span & { route?: UrlTree };

/**
 * The console route a link's target names, or null. Only an absolute path whose first segment is one of the router's
 * own routes is one: a URL with a scheme or a host, a `//` path or a relative one is never routed, and a path the router
 * does not know stays text rather than end in a navigation error.
 */
export function consoleRoute(router: Router, target: string): UrlTree | null {
  if (!/^\/[^/\\]/.test(target)) return null;
  const first = target.slice(1).split(/[/?#]/)[0];
  const known = router.config.some((r) => r.path !== undefined && r.path !== '' && r.path.split('/')[0] === first);
  return known ? router.parseUrl(target) : null;
}

/**
 * One line of a runbook: its plain, `code` and **strong** runs as elements, with no HTML ever built from the text. A
 * link is its words; with `targets`, a link to one of the console's pages is a router link, and any other target
 * follows its words in brackets as text, never as a link.
 */
@Component({
  selector: 'app-runbook-spans',
  imports: [RouterLink],
  // The runs sit side by side in a line of text, so a space Prettier added inside a block would show.
  // prettier-ignore
  template: `@for (s of runs(); track $index) {@switch (s.kind) {@case ('code') {<code>{{ s.text }}</code>}@case ('strong') {<strong>{{ s.text }}</strong>}@default {@if (s.route) {<a [routerLink]="s.route">{{ s.text }}</a>}@else {{{ s.text }}@if (targets() && s.target) { ({{ s.target }})}}}}}`,
})
export class RunbookSpans {
  private readonly router = inject(Router);
  readonly spans = input.required<Span[]>();
  /**
   * Whether a link's target is drawn, for text whose links point somewhere the reader can go: a console page as a link
   * the router follows, anything else as text after the link's words.
   */
  readonly targets = input(false);
  readonly runs = computed<Run[]>(() => {
    if (!this.targets()) return this.spans();
    return this.spans().map((s) => {
      const route = s.target === undefined ? null : consoleRoute(this.router, s.target);
      return route ? { ...s, route } : s;
    });
  });
}
