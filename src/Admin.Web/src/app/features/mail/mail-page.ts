import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { MailWidget } from './widgets/mail-widget';

/**
 * The Mail screen as the legacy screen widget renders it (spec §10): the Mail widget, its filter starting at the
 * `correlationId` the route carries (a link from a trace). Its reads are the widget's. Retired with the legacy
 * screens (phase 6).
 */
@Component({
  selector: 'app-mail-page',
  imports: [MailWidget],
  templateUrl: './mail-page.html',
  styleUrl: './mail-page.css',
})
export class MailPage {
  readonly config = { correlationId: inject(ActivatedRoute).snapshot.queryParamMap.get('correlationId') ?? '' };
}
