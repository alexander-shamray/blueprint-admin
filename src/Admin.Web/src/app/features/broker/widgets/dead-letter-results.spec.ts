import { MoveResultReader, isDryRun } from './dead-letter-results';

describe('MoveResultReader', () => {
  it('reads a dry run: each would- line, and leaves the closing note in the log', () => {
    const reader = new MoveResultReader();

    expect(reader.add('would-replay    m-1 -> rabbitmq://rabbitmq/ordering-catalog-events')).toBe(true);
    expect(reader.add('not found       m-9')).toBe(true);
    expect(reader.add('dry run: nothing left q_error. Pass --execute --record FILE to replay; the read requeued')).toBe(
      false,
    );

    expect(reader.snapshot()).toEqual([
      {
        messageId: 'm-1',
        outcome: 'would-replay',
        destination: 'rabbitmq://rabbitmq/ordering-catalog-events',
        reason: null,
      },
      { messageId: 'm-9', outcome: 'not-found', destination: null, reason: null },
    ]);
    expect(isDryRun(reader.snapshot())).toBe(true);
  });

  it('reads a real run in the past tense, a discard with no destination, and a reason in brackets', () => {
    const reader = new MoveResultReader();
    reader.add('replayed       m-1 -> ep');
    reader.add('discarded      m-2');
    reader.add('refused        m-3 (a fault that names no endpoint)');
    reader.add('not reached    m-4 (the run stopped first)');

    expect(reader.snapshot()).toEqual([
      { messageId: 'm-1', outcome: 'replayed', destination: 'ep', reason: null },
      { messageId: 'm-2', outcome: 'discarded', destination: null, reason: null },
      { messageId: 'm-3', outcome: 'refused', destination: null, reason: 'a fault that names no endpoint' },
      { messageId: 'm-4', outcome: 'not-reached', destination: null, reason: 'the run stopped first' },
    ]);
    expect(isDryRun(reader.snapshot())).toBe(false);
  });

  it('leaves an action word it does not know, and the tool’s other lines, in the log', () => {
    const reader = new MoveResultReader();

    for (const text of [
      'requeued       m-1',
      'truncated      3 more message(s) stay on q_error, past --limit; raise --limit',
      '$ py -3.12 tools/dead-letters/dead_letters.py replay q_error --message-id m-1',
      'unrecorded {"queue": "q"}',
      '',
    ]) {
      expect(reader.add(text)).toBe(false);
    }
    expect(reader.snapshot()).toEqual([]);
  });
});
