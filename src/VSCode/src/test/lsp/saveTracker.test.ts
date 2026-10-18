import * as assert from 'assert';
import { SAVE_WINDOW_MS, SaveTracker } from '../../lsp/saveTracker';

suite('saveTracker', () => {
  const uri = 'file:///a.feature';

  test('a document is not saving until a save begins', () => {
    assert.strictEqual(new SaveTracker().isSaving(uri), false);
  });

  test('a document is saving between begin and end, and only that document', () => {
    const tracker = new SaveTracker();
    tracker.begin(uri);

    assert.strictEqual(tracker.isSaving(uri), true);
    assert.strictEqual(tracker.isSaving('file:///b.feature'), false);

    tracker.end(uri);
    assert.strictEqual(tracker.isSaving(uri), false);
  });

  test('a save that never completes stops counting after the window (failed or cancelled save)', () => {
    let now = 1_000;
    const tracker = new SaveTracker(() => now);
    tracker.begin(uri);

    now += SAVE_WINDOW_MS;
    assert.strictEqual(tracker.isSaving(uri), true, 'still inside the window');

    now += 1;
    assert.strictEqual(tracker.isSaving(uri), false, 'past the window');
  });

  test('a second save restarts the window', () => {
    let now = 0;
    const tracker = new SaveTracker(() => now);
    tracker.begin(uri);
    now += SAVE_WINDOW_MS - 1;
    tracker.begin(uri);
    now += SAVE_WINDOW_MS - 1;

    assert.strictEqual(tracker.isSaving(uri), true);
  });
});
