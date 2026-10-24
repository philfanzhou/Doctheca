import type { DocumentNodeInput, StructureNode } from '../services/structureApi'

export function documentDraft(nodes: StructureNode[]): DocumentNodeInput[] {
  return nodes.map(n => ({ id: n.id, levelId: n.levelId, name: n.name, parseId: n.parseId ?? null,
    startSequence: n.startSequence, endSequence: n.endSequence, children: documentDraft(n.children) }))
}
export function documentRows(nodes: DocumentNodeInput[], depth = 0): { node: DocumentNodeInput; depth: number; siblings: DocumentNodeInput[] }[] {
  return nodes.flatMap(node => [{ node, depth, siblings: nodes }, ...documentRows(node.children, depth + 1)])
}
export function moveSibling(siblings: DocumentNodeInput[], id: string, delta: number): void {
  const index = siblings.findIndex(n => n.id === id)
  const target = index + delta
  if (index < 0 || target < 0 || target >= siblings.length) return
  const [node] = siblings.splice(index, 1)
  siblings.splice(target, 0, node)
}
export function clearBinding(node: DocumentNodeInput): void {
  node.parseId = null; node.startSequence = null; node.endSequence = null
  node.children.forEach(clearBinding)
}
