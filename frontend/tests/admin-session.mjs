// Exercise the actual TypeScript session/client modules with controlled transport timing.
// Real OIDC/CSRF/browser behavior is separately covered by AdminHostedFrontendTests.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, mkdir, readFile, writeFile, symlink, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve, dirname } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'
import { AxiosError } from 'axios'

const deferred = () => { let finish; const promise = new Promise(r => { finish = r }); return { promise, finish } }
const live = { authenticated: true, reason: 'authenticated', username: 'synthetic', expiresAt: '2099-01-01T00:00:00Z' }
async function setup(fn, href = 'http://127.0.0.1/#docs') {
  const root = await mkdtemp(join(tmpdir(), 'doctheca-session-test-'))
  const navigations = []
  globalThis.location = { href, hash: new URL(href).hash, assign: url => navigations.push(url) }
  globalThis.history = { replaceState: (_s, _t, url) => { location.href = new URL(url, location.href).href } }
  try {
    await writeFile(join(root, 'package.json'), '{"type":"module"}')
    await symlink(resolve('node_modules'), join(root, 'node_modules'))
    for (const file of ['composables/useAdminSession', 'services/authApi', 'services/httpClient', 'services/error', 'services/documentApi', 'services/parseApi']) {
      const source = await readFile(`src/${file}.ts`, 'utf8')
      const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
        .replace(/from '(\.\.?\/[^']+)'/g, "from '$1.js'")
      await mkdir(dirname(join(root, file)), { recursive: true }); await writeFile(join(root, file + '.js'), js)
    }
    const module = await import(pathToFileURL(join(root, 'composables/useAdminSession.js')))
    const clients = await import(pathToFileURL(join(root, 'services/httpClient.js')))
    const calls = []
    clients.authHttpClient.defaults.adapter = async config => {
      calls.push(config)
      return response(config, config.url.endsWith('/csrf') ? { requestToken: 'synthetic-csrf' } : config.url.endsWith('/logout') ? { reason: 'localSignedOut', logoutUrl: null } : live)
    }
    const documentApi = await import(pathToFileURL(join(root, 'services/documentApi.js')))
    const parseApi = await import(pathToFileURL(join(root, 'services/parseApi.js')))
    await fn({ documentApi, parseApi, ...module, ...clients, state: module.useAdminSession(), calls, navigations })
  } finally { await rm(root, { recursive: true, force: true }) }
}
const response = (config, data) => ({ config, data: { success: true, data }, status: 200, headers: {}, statusText: 'OK' })
const rejection = (config, status) => Promise.reject(new AxiosError('fixed failure', undefined, config, undefined, { config, status, data: {}, headers: {}, statusText: '' }))

test('initialization and CSRF deduplicate; all write methods include CSRF', () => setup(async h => {
  await Promise.all([h.state.initialize(), h.state.initialize()])
  assert.equal(h.calls.length, 1)
  const writes = []
  h.httpClient.defaults.adapter = async config => { writes.push(config); return response(config, {}) }
  await Promise.all(['post', 'put', 'patch', 'delete'].map(method => h.httpClient.request({ url: '/admin/write', method })))
  assert.equal(h.calls.filter(c => c.url.endsWith('/csrf')).length, 1)
  assert.equal(writes.length, 4)
  for (const config of writes) assert.equal(config.headers.get('X-CSRF-TOKEN'), 'synthetic-csrf')
}))
test('401 never refreshes or replays; late initialization cannot revive a revoked UI', () => setup(async h => {
  const pending = deferred()
  h.authHttpClient.defaults.adapter = config => pending.promise.then(() => response(config, live))
  const initializing = h.state.initialize()
  let attempts = 0
  h.httpClient.defaults.adapter = config => { attempts++; return rejection(config, 401) }
  await Promise.allSettled([h.httpClient.get('/admin/a'), h.httpClient.get('/admin/b')])
  pending.finish(); await initializing
  assert.equal(attempts, 2); assert.equal(h.state.status.value, 'anonymous'); assert.equal(h.state.session.value, null)
  assert.equal(h.navigations.length, 0)
}))
test('late CSRF cannot authorize a write after expiry; CSRF failure sends no write', () => setup(async h => {
  await h.state.initialize()
  const pending = deferred()
  h.authHttpClient.defaults.adapter = config => pending.promise.then(() => response(config, { requestToken: 'synthetic-csrf' }))
  let writes = 0
  h.httpClient.defaults.adapter = config => { if (config.method === 'get') return rejection(config, 401); writes++; return response(config, {}) }
  const write = h.httpClient.post('/admin/write')
  await new Promise(r => setImmediate(r))
  await h.httpClient.get('/admin/expired').catch(() => {})
  pending.finish(); await assert.rejects(write)
  assert.equal(writes, 0)
}))
test('403 does not redirect/retry; concurrent logout sends one CSRF-protected POST', () => setup(async h => {
  await h.state.initialize()
  h.httpClient.defaults.adapter = config => rejection(config, 403)
  await h.httpClient.get('/admin/forbidden').catch(() => {})
  assert.equal(h.state.status.value, 'authenticated'); assert.equal(h.state.forbiddenSignal.value, 1)
  await Promise.all([h.state.logout(), h.state.logout()])
  const exits = h.calls.filter(c => c.url.endsWith('/logout'))
  assert.equal(exits.length, 1); assert.equal(exits[0].headers.get('X-CSRF-TOKEN'), 'synthetic-csrf')
  assert.equal(h.state.status.value, 'anonymous'); assert.match(h.state.message.value, /本地已退出/)
}))
test('logout network failure clears UI without claiming revocation', () => setup(async h => {
  await h.state.initialize()
  h.authHttpClient.defaults.adapter = async config => { if (config.url.endsWith('/csrf')) return response(config, { requestToken: 'synthetic-csrf' }); throw new Error('offline') }
  await h.state.logout(); assert.equal(h.state.session.value, null); assert.match(h.state.message.value, /退出失败/)
}))
test('return URL is an exact whitelist and double click navigates once', () => setup(async h => {
  for (const hash of ['#search/other', '#docs/abc', '#detail/id/extra', '#detail/a?code=x', '#unknown', '#detail/%2f']) assert.equal(h.adminReturnUrl(hash), '/')
  assert.equal(h.adminReturnUrl('#detail/A_b-12'), '/#detail/A_b-12')
  h.state.login(); h.state.login(); assert.equal(h.navigations.length, 1)
  assert.equal(h.navigations[0], '/admin/auth/oidc/start?returnUrl=%2F%23docs')
}))
test('only fixed single auth result is displayed and removed', () => setup(async h => {
  h.authHttpClient.defaults.adapter = config => rejection(config, 401)
  await h.state.initialize(); assert.match(h.state.message.value, /已取消登录/)
  assert.equal(new URL(location.href).search, '')
}, 'http://127.0.0.1/?authError=cancelled#docs'))
test('duplicate or unknown result never echoes external text', () => setup(async h => {
  h.authHttpClient.defaults.adapter = config => rejection(config, 401)
  await h.state.initialize(); assert.doesNotMatch(h.state.message.value, /external/)
  assert.equal(new URL(location.href).search, '')
}, 'http://127.0.0.1/?authError=external&authError=cancelled'))

test('all existing business write services consume the CSRF boundary', () => setup(async h => {
  await h.state.initialize()
  const writes = []
  h.httpClient.defaults.adapter = async config => { writes.push(config); return response(config, {}) }
  await h.documentApi.uploadDocumentFile(new File(['synthetic'], 'synthetic.txt'))
  await h.documentApi.parseDocumentFile('synthetic')
  await h.documentApi.deleteDocumentFile('synthetic')
  await h.parseApi.deleteDocumentParse('synthetic')
  assert.deepEqual(writes.map(c => c.method), ['post', 'post', 'delete', 'delete'])
  for (const config of writes) assert.equal(config.headers.get('X-CSRF-TOKEN'), 'synthetic-csrf')
  assert.equal(h.calls.filter(c => c.url.endsWith('/csrf')).length, 1)
}))
test('CSRF retrieval failure prevents the original business write', () => setup(async h => {
  await h.state.initialize()
  h.authHttpClient.defaults.adapter = config => rejection(config, 403)
  let writes = 0
  h.httpClient.defaults.adapter = async config => { writes++; return response(config, {}) }
  await assert.rejects(h.documentApi.parseDocumentFile('synthetic'))
  assert.equal(writes, 0); assert.equal(h.state.status.value, 'authenticated')
}))
