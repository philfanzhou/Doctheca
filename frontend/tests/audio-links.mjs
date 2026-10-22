// Exercises the structure audio page helpers (issue #90) transpiled from TypeScript.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'

async function load() {
  const root = await mkdtemp(join(tmpdir(), 'doctheca-audio-links-'))
  const source = await readFile('src/utils/audioLinks.ts', 'utf8')
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
  await writeFile(join(root, 'audioLinks.mjs'), js)
  try {
    return await import(pathToFileURL(join(root, 'audioLinks.mjs')))
  } finally {
    await rm(root, { recursive: true, force: true })
  }
}

test('node options list every node with its path in reading order', async () => {
  const a = await load()
  const options = a.nodeOptions([
    { id: 'u1', name: 'Unit 1 X', children: [{ id: 's1', name: 'Section A', children: [] }] },
    { id: 'u2', name: 'Unit 2 Y', children: [] },
  ])
  assert.deepEqual(options, [
    { id: 'u1', label: 'Unit 1 X', depth: 0 },
    { id: 's1', label: 'Unit 1 X > Section A', depth: 1 },
    { id: 'u2', label: 'Unit 2 Y', depth: 0 },
  ])
})

test('link states flag only unlinked files that nobody reviewed', async () => {
  const a = await load()
  assert.equal(a.linkState({ id: '1', nodeId: 'n', matchStatus: 'matched', linkSource: 'prefill' }).flagged, false)
  assert.equal(a.linkState({ id: '1', nodeId: null, matchStatus: 'folder_name_mismatch', linkSource: null }).label, '文件夹与文件名不一致')
  assert.equal(a.linkState({ id: '1', nodeId: null, matchStatus: 'folder_name_mismatch', linkSource: null }).flagged, true)
  assert.equal(a.linkState({ id: '1', nodeId: 'n', matchStatus: 'folder_name_mismatch', linkSource: 'manual' }).label, '已手动关联')
  assert.equal(a.linkState({ id: '1', nodeId: null, matchStatus: 'matched', linkSource: 'manual' }).flagged, false)
  assert.equal(a.linkState({ id: '1', nodeId: null, matchStatus: 'node_removed', linkSource: null }).flagged, true)
  assert.equal(a.linkState({ id: '1', nodeId: null, matchStatus: 'ambiguous', linkSource: null }).label, '匹配不唯一')
})

test('the save payload carries only changed links and the removed files', async () => {
  const a = await load()
  const saved = [
    { id: 'a', nodeId: 'n1', matchStatus: 'matched', linkSource: 'prefill' },
    { id: 'b', nodeId: null, matchStatus: 'no_match', linkSource: null },
    { id: 'c', nodeId: 'n2', matchStatus: 'matched', linkSource: 'prefill' },
  ]
  const payload = a.savePayload(saved, { a: 'n1', b: 'n3', c: null }, new Set(['c']))
  assert.deepEqual(payload, { links: [{ id: 'b', nodeId: 'n3' }], removeIds: ['c'] })
  assert.deepEqual(a.savePayload(saved, {}, new Set()), { links: [], removeIds: [] })
})

test('system files of folder picks are recognised', async () => {
  const a = await load()
  assert.equal(a.isSystemFile('官方音频/.DS_Store'), true)
  assert.equal(a.isSystemFile('__MACOSX/unit1/._a.mp3'), true)
  assert.equal(a.isSystemFile('unit1/9s01Unit1SectionA1b.mp3'), false)
  assert.equal(a.formatBytes(20 * 1024 * 1024), '20 MiB')
})
