import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  OPEN_TAG_LINK_COMMAND,
  TagLinkDecorations,
  createTagLinkMiddleware,
  isOpenableUrl,
  openTagLink,
  registerTagLinkRefresh,
  retargetLink,
} from '../../lsp/tagLinks';

const range = new vscode.Range(1, 0, 1, 11);
const issueUrl = 'https://github.com/org/repo/issues/1234';

suite('tagLinks', () => {
  test('isOpenableUrl allows only http and https', () => {
    assert.strictEqual(isOpenableUrl('https://example.com/a'), true);
    assert.strictEqual(isOpenableUrl('http://example.com/a'), true);
    assert.strictEqual(isOpenableUrl('file:///C:/Windows/System32/calc.exe'), false);
    assert.strictEqual(isOpenableUrl('command:workbench.action.reloadWindow'), false);
    assert.strictEqual(isOpenableUrl('not a url'), false);
    assert.strictEqual(isOpenableUrl(undefined), false);
  });

  test('retargetLink points the link at the internal command and keeps the URL as tooltip', () => {
    const retargeted = retargetLink(new vscode.DocumentLink(range, vscode.Uri.parse(issueUrl)));

    assert.ok(retargeted);
    assert.strictEqual(retargeted.range, range);
    assert.strictEqual(retargeted.target?.scheme, 'command');
    assert.strictEqual(retargeted.target?.path, OPEN_TAG_LINK_COMMAND);
    assert.deepStrictEqual(JSON.parse(decodeURIComponent(retargeted.target.query)), [issueUrl]);
    assert.strictEqual(retargeted.tooltip, issueUrl);
  });

  test('retargetLink drops links that are not openable web links', () => {
    assert.strictEqual(
      retargetLink(new vscode.DocumentLink(range, vscode.Uri.file('/etc/passwd'))),
      undefined,
    );
    assert.strictEqual(retargetLink(new vscode.DocumentLink(range)), undefined);
  });

  test('middleware retargets the links returned by the built-in feature', async () => {
    const middleware = createTagLinkMiddleware();
    const next = () => [
      new vscode.DocumentLink(range, vscode.Uri.parse(issueUrl)),
      new vscode.DocumentLink(range, vscode.Uri.file('/etc/passwd')),
    ];

    const result = await middleware.provideDocumentLinks!({} as never, {} as never, next);

    assert.strictEqual(result?.length, 1);
    assert.strictEqual(result?.[0].target?.scheme, 'command');
  });

  test('middleware passes a null result through', async () => {
    const middleware = createTagLinkMiddleware();

    const result = await middleware.provideDocumentLinks!({} as never, {} as never, () => null);

    assert.ok(result === null || result === undefined);
  });

  suite('TagLinkDecorations (issue #921)', () => {
    const docUri = vscode.Uri.parse('untitled:/a.feature');

    /** A fake editor on `uri` that records every setDecorations call. */
    function fakeEditor(uri: vscode.Uri) {
      const calls: vscode.Range[][] = [];
      const editor = {
        document: { uri },
        setDecorations: (_type: unknown, ranges: vscode.Range[]) => calls.push(ranges),
      } as unknown as vscode.TextEditor;
      return { editor, calls };
    }

    function create(editors: vscode.TextEditor[]) {
      const type = vscode.window.createTextEditorDecorationType({ textDecoration: 'underline' });
      return new TagLinkDecorations(() => editors, type);
    }

    test('record paints the visible editor of that document with the link ranges', () => {
      const { editor, calls } = fakeEditor(docUri);
      const decorations = create([editor]);

      decorations.record(docUri, [range]);

      assert.deepStrictEqual(calls, [[range]]);
      assert.deepStrictEqual(decorations.rangesFor(docUri), [range]);
      decorations.dispose();
    });

    test('record leaves editors of other documents alone', () => {
      const { editor, calls } = fakeEditor(vscode.Uri.parse('untitled:/other.feature'));
      const decorations = create([editor]);

      decorations.record(docUri, [range]);

      assert.deepStrictEqual(calls, []);
      decorations.dispose();
    });

    test('an empty answer clears the styling', () => {
      const { editor, calls } = fakeEditor(docUri);
      const decorations = create([editor]);
      decorations.record(docUri, [range]);

      decorations.record(docUri, []);

      assert.deepStrictEqual(calls[calls.length - 1], []);
      assert.deepStrictEqual(decorations.rangesFor(docUri), []);
      decorations.dispose();
    });

    test('middleware records the retargeted links, dropping the non-openable ones', async () => {
      const { editor, calls } = fakeEditor(docUri);
      const decorations = create([editor]);
      const middleware = createTagLinkMiddleware(decorations);
      const other = new vscode.Range(3, 0, 3, 5);
      const next = () => [
        new vscode.DocumentLink(range, vscode.Uri.parse(issueUrl)),
        new vscode.DocumentLink(other, vscode.Uri.file('/etc/passwd')),
      ];

      await middleware.provideDocumentLinks!({ uri: docUri } as never, {} as never, next);

      assert.deepStrictEqual(calls, [[range]]);
      decorations.dispose();
    });

    test('middleware keeps the last known styling when the request returns nothing', async () => {
      const { editor, calls } = fakeEditor(docUri);
      const decorations = create([editor]);
      decorations.record(docUri, [range]);
      calls.length = 0;
      const middleware = createTagLinkMiddleware(decorations);

      await middleware.provideDocumentLinks!({ uri: docUri } as never, {} as never, () => null);

      assert.deepStrictEqual(calls, []);
      assert.deepStrictEqual(decorations.rangesFor(docUri), [range]);
      decorations.dispose();
    });
  });

  test('openTagLink opens an http(s) URL', async () => {
    const opened: string[] = [];

    await openTagLink(issueUrl, (uri) => {
      opened.push(uri.toString());
      return Promise.resolve(true);
    });

    assert.deepStrictEqual(opened, [vscode.Uri.parse(issueUrl).toString()]);
  });

  test('openTagLink ignores anything that is not an openable web link', async () => {
    const opened: string[] = [];
    const opener = (uri: vscode.Uri) => {
      opened.push(uri.toString());
      return Promise.resolve(true);
    };

    await openTagLink('file:///C:/Windows/System32/calc.exe', opener);
    await openTagLink(42, opener);
    await openTagLink(undefined, opener);

    assert.deepStrictEqual(opened, []);
  });

  test('registerTagLinkRefresh nudges once for a burst of refresh signals', async () => {
    const emitter = new vscode.EventEmitter<void>();
    let nudges = 0;
    const subscription = registerTagLinkRefresh(emitter.event, () => nudges++, 20);

    emitter.fire();
    emitter.fire();
    emitter.fire();
    await new Promise((resolve) => setTimeout(resolve, 80));

    assert.strictEqual(nudges, 1);
    subscription.dispose();
    emitter.dispose();
  });

  test('registerTagLinkRefresh stops nudging once disposed', async () => {
    const emitter = new vscode.EventEmitter<void>();
    let nudges = 0;
    const subscription = registerTagLinkRefresh(emitter.event, () => nudges++, 20);

    emitter.fire();
    subscription.dispose();
    emitter.fire();
    await new Promise((resolve) => setTimeout(resolve, 80));

    assert.strictEqual(nudges, 0);
    emitter.dispose();
  });
});
