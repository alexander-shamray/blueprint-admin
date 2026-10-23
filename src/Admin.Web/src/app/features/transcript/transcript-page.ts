import { Component } from '@angular/core';
import { TranscriptWidget } from './widgets/transcript-widget';

/**
 * The Transcript screen as the legacy screen widget renders it (spec §10): the Transcript widget. Retired with the
 * legacy screens (phase 6).
 */
@Component({
  selector: 'app-transcript-page',
  imports: [TranscriptWidget],
  templateUrl: './transcript-page.html',
  styleUrl: './transcript-page.css',
})
export class TranscriptPage {}
