import { Component } from '@angular/core';
import { ErasureWidget } from './widgets/erasure-widget';

/**
 * The Erasure screen as the legacy screen widget renders it (spec §10): the Erasure widget. The request it watches is
 * `ErasureRun`'s. Retired with the legacy screens (phase 6).
 */
@Component({
  selector: 'app-erasure-page',
  imports: [ErasureWidget],
  templateUrl: './erasure-page.html',
  styleUrl: './erasure-page.css',
})
export class ErasurePage {}
