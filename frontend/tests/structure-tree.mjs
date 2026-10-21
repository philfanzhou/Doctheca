// Exercises the actual structure-tree editing module (issue #89) transpiled from TypeScript.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'

async function load() {
  const root = await mkdtemp(join(tmpdir(), 'doctheca-structure-tree-'))
  const source = await readFile('src/utils/structureTree.ts', 'utf8')
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
  await writeFile(join(root, 'structureTree.mjs'), js)
  try {
    return await import(pathToFileURL(join(root, 'structureTree.mjs')))
  } finally {
    await rm(root, { recursive: true, force: true })
  }
}

// Level names are arbitrary data: the module must not depend on them.
const levels = [
  { id: 'top', name: 'L-one', parentLevelId: null },
  { id: 'mid', name: 'L-two', parentLevelId: 'top' },
  { id: 'leaf', name: 'L-three', parentLevelId: 'mid' },
]
const add = (tree, t, id, levelId, start, end) => {
  const result = t.insertNode(tree, levels, { id, levelId, name: id, startSequence: start, endSequence: end })
  assert.equal(result.ok, true, result.reason)
  return result.nodes
}

test('allowed levels follow the deepest containing node', async () => {
  const t = await load()
  let tree = add([], t, 'u1', 'top', 0, 49)
  assert.deepEqual(t.allowedLevels(tree, levels, 60, 70).map((l) => l.id), ['top'])
  assert.deepEqual(t.allowedLevels(tree, levels, 5, 10).map((l) => l.id), ['mid'])
  tree = add(tree, t, 's1', 'mid', 0, 19)
  assert.deepEqual(t.allowedLevels(tree, levels, 2, 3).map((l) => l.id), ['leaf'])
})

test('children are inserted inside their parent and siblings stay ordered', async () => {
  const t = await load()
  let tree = add([], t, 'u2', 'top', 50, 99)
  tree = add(tree, t, 'u1', 'top', 0, 49)
  tree = add(tree, t, 'b', 'mid', 20, 39)
  tree = add(tree, t, 'a', 'mid', 0, 19)
  assert.deepEqual(t.flatten(tree).map((f) => `${f.depth}:${f.node.id}`), ['0:u1', '1:a', '1:b', '0:u2'])
  assert.equal(t.countNodes(tree), 4)
})

test('three levels nest, and a node may not overlap a sibling of the same parent', async () => {
  const t = await load()
  let tree = add([], t, 'u1', 'top', 0, 99)
  tree = add(tree, t, 's1', 'mid', 0, 49)
  tree = add(tree, t, 'x1', 'leaf', 0, 9)
  tree = add(tree, t, 'x2', 'leaf', 10, 19)
  assert.deepEqual(t.flatten(tree).map((f) => `${f.depth}:${f.node.id}`), ['0:u1', '1:s1', '2:x1', '2:x2'])
  const clash = t.insertNode(tree, levels, { id: 's2', levelId: 'mid', name: 's2', startSequence: 40, endSequence: 60 })
  assert.equal(clash.ok, false)
  assert.match(clash.reason, /s1/)
  tree = t.removeNode(tree, 's1')
  assert.deepEqual(t.flatten(tree).map((f) => f.node.id), ['u1'])
})

test('invalid inserts are rejected with a reason', async () => {
  const t = await load()
  const tree = add([], t, 'u1', 'top', 0, 49)
  for (const input of [
    { id: 'n', levelId: 'mid', name: 'n', startSequence: 40, endSequence: 60 }, // crosses parent end
    { id: 'n', levelId: 'top', name: 'n', startSequence: 40, endSequence: 60 }, // overlaps sibling
    { id: 'n', levelId: 'mid', name: 'n', startSequence: 60, endSequence: 70 }, // no parent there
    { id: 'n', levelId: 'top', name: ' ', startSequence: 60, endSequence: 70 }, // empty name
    { id: 'n', levelId: 'top', name: 'n', startSequence: 70, endSequence: 60 }, // start after end
  ]) {
    assert.equal(t.insertNode(tree, levels, input).ok, false, JSON.stringify(input))
  }
})

test('range edits keep children inside and avoid siblings; rename never mutates the input', async () => {
  const t = await load()
  let tree = add([], t, 'u1', 'top', 0, 49)
  tree = add(tree, t, 'u2', 'top', 50, 99)
  tree = add(tree, t, 's1', 'mid', 10, 19)
  assert.equal(t.setNodeRange(tree, 'u1', 15, 49).ok, false) // would drop child s1
  assert.equal(t.setNodeRange(tree, 'u1', 0, 60).ok, false) // overlaps u2
  assert.equal(t.setNodeRange(tree, 's1', 10, 55).ok, false) // leaves parent
  const moved = t.setNodeRange(tree, 'u1', 0, 30)
  assert.equal(moved.ok, true)
  assert.equal(t.flatten(moved.nodes)[0].node.endSequence, 30)
  const renamed = t.renameNode(tree, 's1', 'renamed')
  assert.equal(t.flatten(renamed)[1].node.name, 'renamed')
  assert.equal(t.flatten(tree)[1].node.name, 's1')
})
