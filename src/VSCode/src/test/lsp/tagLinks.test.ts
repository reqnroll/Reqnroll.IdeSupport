import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  OPEN_TAG_LINK_COMMAND,
  createTagLinkMiddleware,
  isOpenableUrl,
  openTagLink,
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
});
