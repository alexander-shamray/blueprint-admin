import { OutputLine } from '../../core/host/host-types';
import { composeProgress } from './compose-progress';

const lines = (...texts: string[]): OutputLine[] =>
  texts.map((text, sequence) => ({ sequence, at: '', stream: 'Stderr', text }));

describe('composeProgress', () => {
  it('keeps the last verb a container printed, in first-seen order, and counts the settled ones', () => {
    const progress = composeProgress(
      lines(
        ' Container a-1  Creating',
        ' Container b-1  Creating',
        ' Container a-1  Created',
        ' Container a-1  Healthy',
        ' Container b-1  Waiting',
      ),
    );

    expect(progress.services).toEqual([
      { name: 'a-1', verb: 'Healthy' },
      { name: 'b-1', verb: 'Waiting' },
    ]);
    expect(progress.settled).toBe(1);
  });

  it('counts Started as not settled, because --wait goes on to Healthy', () => {
    expect(composeProgress(lines(' Container a-1  Started')).settled).toBe(0);
  });

  it('lists neither volumes nor networks nor prose', () => {
    const progress = composeProgress(
      lines(' Volume v  Created', ' Network n  Created', 'exited with something', ' Container a-1 is unhealthy'),
    );

    expect(progress.services).toEqual([]);
  });
});
