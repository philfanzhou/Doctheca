// Pure editing logic of a hand-marked structure tree (issue #89). Nodes are inclusive ranges of
// block sequences; level names are configured data and never appear in this file. The backend
// re-validates every save, so these checks only give early, friendly feedback.

export interface TreeLevel {
  id: string
  name: string
  parentLevelId: string | null
}

export interface TreeNode {
  id: string
  levelId: string
  name: string
  startSequence: number
  endSequence: number
  children: TreeNode[]
}

export type TreeResult = { ok: true; nodes: TreeNode[] } | { ok: false; reason: string }

export interface NewNode {
  id: string
  levelId: string
  name: string
  startSequence: number
  endSequence: number
}

const contains = (node: TreeNode, start: number, end: number) =>
  node.startSequence <= start && end <= node.endSequence

const overlaps = (node: TreeNode, start: number, end: number) =>
  node.startSequence <= end && start <= node.endSequence

const byStart = (a: TreeNode, b: TreeNode) => a.startSequence - b.startSequence

/** Deep copy so edits never mutate the caller's reactive state. */
export function cloneTree(nodes: TreeNode[]): TreeNode[] {
  return nodes.map((node) => ({ ...node, children: cloneTree(node.children) }))
}

/** The chain of nodes (root first) whose ranges contain the whole [start, end] range. */
export function containingPath(nodes: TreeNode[], start: number, end: number): TreeNode[] {
  const path: TreeNode[] = []
  let level = nodes
  for (;;) {
    const next = level.find((node) => contains(node, start, end))
    if (!next) return path
    path.push(next)
    level = next.children
  }
}

/**
 * Levels that a new node over [start, end] may use: the child levels of the deepest node that
 * contains the range, or the top levels when no node contains it. (A shallower depth is never
 * possible: the containing node at that depth would overlap the new node.)
 */
export function allowedLevels(nodes: TreeNode[], levels: TreeLevel[], start: number, end: number): TreeLevel[] {
  const path = containingPath(nodes, start, end)
  const parentLevelId = path.length > 0 ? path[path.length - 1].levelId : null
  return levels.filter((level) => level.parentLevelId === parentLevelId)
}

/**
 * Inserts a node under the nearest containing node of its level's parent level (or at the root
 * for a top level). Siblings at that position share the parent and must not overlap it.
 */
export function insertNode(nodes: TreeNode[], levels: TreeLevel[], input: NewNode): TreeResult {
  const level = levels.find((l) => l.id === input.levelId)
  if (!level) return { ok: false, reason: '请选择层级' }
  if (!input.name.trim()) return { ok: false, reason: '请填写节点名称' }
  if (input.startSequence > input.endSequence) return { ok: false, reason: '起点不能晚于终点' }

  const tree = cloneTree(nodes)
  const path = containingPath(tree, input.startSequence, input.endSequence)
  let siblings = tree
  if (level.parentLevelId !== null) {
    const parent = path.find((node) => node.levelId === level.parentLevelId)
    if (!parent) {
      const parentName = levels.find((l) => l.id === level.parentLevelId)?.name ?? ''
      return { ok: false, reason: `选区必须位于某个「${parentName}」节点之内` }
    }
    siblings = parent.children
  }

  const clash = siblings.find((sibling) => overlaps(sibling, input.startSequence, input.endSequence))
  if (clash) {
    return { ok: false, reason: `与已有节点「${clash.name}」(#${clash.startSequence}–#${clash.endSequence}) 重叠` }
  }

  siblings.push({
    id: input.id,
    levelId: level.id,
    name: input.name.trim(),
    startSequence: input.startSequence,
    endSequence: input.endSequence,
    children: [],
  })
  siblings.sort(byStart)
  return { ok: true, nodes: tree }
}

/** Removes a node together with its descendants. */
export function removeNode(nodes: TreeNode[], id: string): TreeNode[] {
  return nodes
    .filter((node) => node.id !== id)
    .map((node) => ({ ...node, children: removeNode(node.children, id) }))
}

/** Renames a node. */
export function renameNode(nodes: TreeNode[], id: string, name: string): TreeNode[] {
  return nodes.map((node) =>
    node.id === id ? { ...node, name, children: node.children } : { ...node, children: renameNode(node.children, id, name) }
  )
}

/** Changes a node's range when it still holds its children, fits its parent and avoids its siblings. */
export function setNodeRange(nodes: TreeNode[], id: string, start: number, end: number): TreeResult {
  if (start > end) return { ok: false, reason: '起点不能晚于终点' }
  const tree = cloneTree(nodes)
  const located = locate(tree, id, null)
  if (!located) return { ok: false, reason: '节点不存在' }
  const { node, parent, siblings } = located
  if (parent && !contains(parent, start, end)) {
    return { ok: false, reason: `范围必须位于上级节点「${parent.name}」之内` }
  }
  const outside = node.children.find((child) => !(start <= child.startSequence && child.endSequence <= end))
  if (outside) return { ok: false, reason: `范围必须包含下级节点「${outside.name}」` }
  const clash = siblings.find((sibling) => sibling !== node && overlaps(sibling, start, end))
  if (clash) return { ok: false, reason: `与同级节点「${clash.name}」重叠` }
  node.startSequence = start
  node.endSequence = end
  siblings.sort(byStart)
  return { ok: true, nodes: tree }
}

function locate(
  siblings: TreeNode[],
  id: string,
  parent: TreeNode | null
): { node: TreeNode; parent: TreeNode | null; siblings: TreeNode[] } | null {
  for (const node of siblings) {
    if (node.id === id) return { node, parent, siblings }
    const found = locate(node.children, id, node)
    if (found) return found
  }
  return null
}

export interface FlatNode {
  node: TreeNode
  depth: number
}

/** Pre-order list with depth (parents first, siblings by start). */
export function flatten(nodes: TreeNode[], depth = 0, out: FlatNode[] = []): FlatNode[] {
  for (const node of [...nodes].sort(byStart)) {
    out.push({ node, depth })
    flatten(node.children, depth + 1, out)
  }
  return out
}

export function countNodes(nodes: TreeNode[]): number {
  return nodes.reduce((sum, node) => sum + 1 + countNodes(node.children), 0)
}
