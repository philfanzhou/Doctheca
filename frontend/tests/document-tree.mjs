import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'
const root = await mkdtemp(join(tmpdir(), 'doctheca-document-tree-'))
const js = ts.transpileModule(await readFile('src/utils/documentTree.ts', 'utf8'), { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
await writeFile(join(root, 'tree.mjs'), js)
const t = await import(pathToFileURL(join(root, 'tree.mjs')))
await rm(root, { recursive: true, force: true })
const node = (id, start, children = []) => ({ id, name: id, levelId: 'arbitrary', parseId: start === null ? null : 'real-parse', startSequence: start, endSequence: start, children, sortOrder: 0 })
test('document order survives binding, moving and unbinding with stable nested identity', () => {
  const draft = t.documentDraft([node('later', 20, [node('child', 20)]), node('first', 0), node('unbound', null)])
  assert.deepEqual(t.documentRows(draft).map(r => r.node.id), ['later', 'child', 'first', 'unbound'])
  t.moveSibling(draft, 'unbound', -1)
  assert.deepEqual(draft.map(n => n.id), ['later', 'unbound', 'first'])
  t.clearBinding(draft[0])
  assert.equal(draft[0].children[0].parseId, null)
  assert.equal(draft[0].children[0].startSequence, null)
  assert.deepEqual(t.documentRows(draft).map(r => [r.node.id, r.depth]), [['later', 0], ['child', 1], ['unbound', 0], ['first', 0]])
})
