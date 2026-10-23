import { Component } from '@angular/core';
import { MailWidget } from './widgets/mail-widget';

/**
 * The Mail screen as the legacy screen widget renders it (spec §10): the Mail widget, which reads the route's
 * `?correlationId=` itself. Its reads are the widget's. Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-mail-page',
  imports: [MailWidget],
  templateUrl: './mail-page.html',
  styleUrl: './mail-page.css',
})
export class MailPage {}
