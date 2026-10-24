// Runs as the hosted runner UID, with only the explicitly mounted build tools/packages.
const fs = require('node:fs');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const state = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const mode = process.argv[3];
const root = path.resolve(__dirname, '..');
const bin = path.join(root, 'src/Tests/Doctheca.Tests/bin/Release/net10.0');

function requireEqual(actual, expected, code) {
  if (actual !== expected) throw new Error(code);
}
function readTool(command, args) {
  const result = spawnSync(command, args, { encoding: 'utf8' });
  if (result.status !== 0) throw new Error('CI_TEST_TOOL_UNAVAILABLE');
  return result.stdout.trim();
}
async function gates(overrides = {}) {
  const expected = { ...state, ...overrides };
  requireEqual(process.version, expected.nodeVersion, 'CI_TEST_NODE_MISMATCH');
  requireEqual(readTool(state.npm, ['--version']), expected.npmVersion, 'CI_TEST_NPM_MISMATCH');
  for (const [key, args] of [['sdk', ['--version']], ['sdks', ['--list-sdks']], ['runtimes', ['--list-runtimes']]])
    requireEqual(readTool(state.dotnet, args), expected[key], 'CI_TEST_RUNTIME_MISMATCH');
  try { fs.accessSync(path.join(expected.packages, 'microsoft.playwright', state.playwrightVersion), fs.constants.R_OK); }
  catch { throw new Error('CI_TEST_PACKAGES_MISSING'); }
  const driver = path.join(bin, '.playwright/package');
  requireEqual(require(path.join(driver, 'package.json')).version, expected.playwrightVersion, 'CI_TEST_PLAYWRIGHT_DRIVER_MISMATCH');
  const memory = fs.statfsSync('/dev/shm');
  if (memory.bsize * memory.blocks < 512 * 1024 * 1024) throw new Error('CI_TEST_SHARED_MEMORY_TOO_SMALL');
  const playwright = require(driver);
  try { fs.accessSync(playwright.chromium.executablePath(), fs.constants.X_OK); }
  catch { throw new Error('CI_TEST_BROWSER_MISSING'); }
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.setContent('<p>CI browser ready</p>');
    requireEqual(await page.textContent('p'), 'CI browser ready', 'CI_TEST_BROWSER_PROBE_FAILED');
    fs.writeFileSync(path.join(path.dirname(process.argv[2]), 'browser.json'), JSON.stringify({
      chromium: browser.version(), playwright: state.playwrightVersion, node: process.version,
      sdk: state.sdk, sharedMemoryBytes: memory.bsize * memory.blocks,
    }));
    console.log(`CI browser ready: Chromium ${browser.version()}, Playwright ${state.playwrightVersion}, Node ${process.version}, SDK ${state.sdk}`);
  } finally { await browser.close(); }
}
function child(command, args, options = {}) {
  return new Promise(resolve => {
    const processChild = spawn(command, args, { stdio: 'inherit', ...options });
    const handlers = new Map(['SIGTERM', 'SIGINT'].map(signal => {
      const handler = () => processChild.kill(signal);
      process.on(signal, handler); return [signal, handler];
    }));
    const finish = code => {
      for (const [name, handler] of handlers) process.removeListener(name, handler);
      resolve(code);
    };
    processChild.on('error', () => finish(1));
    processChild.on('exit', (code, signal) => finish(code ?? (signal === 'SIGINT' ? 130 : 143)));
  });
}
async function main() {
  await gates();
  if (mode === 'probe') {
    // Exercise runtime/package identity failures against the actual mounted Linux tools.
    for (const override of [{ nodeVersion: 'v0.0.0' }, { sdk: '0.0.0' },
      { packages: '/missing-ci-packages' }, { playwrightVersion: '0.0.0' }]) {
      let rejected = false;
      try { await gates(override); } catch { rejected = true; }
      if (!rejected) throw new Error('CI_TEST_FAILURE_PROBE_NOT_REJECTED');
    }
    requireEqual(await child(state.node, ['-e', 'process.exit(37)']), 37, 'CI_TEST_FAILURE_STATUS_LOST');
    requireEqual(await child('/missing-ci-node', []), 1, 'CI_TEST_MISSING_NODE_NOT_REJECTED');
    requireEqual(await child(state.node, ['-e', 'process.kill(process.pid, "SIGTERM")']), 143, 'CI_TEST_SIGNAL_STATUS_LOST');
    const missingBrowser = `require(${JSON.stringify(path.join(bin, '.playwright/package'))}).chromium.launch({headless:true}).then(b=>b.close().then(()=>process.exit(1))).catch(()=>process.exit(43))`;
    requireEqual(await child(state.node, ['-e', missingBrowser], { env: { ...process.env, PLAYWRIGHT_BROWSERS_PATH: '/missing-ci-browser' } }), 43, 'CI_TEST_MISSING_BROWSER_NOT_REJECTED');
    console.log('CI environment failure probes passed: Node/runtime/packages/driver mismatch, missing browser, nonzero and termination status.');
    return 0;
  }
  if (mode === 'prepare') return 0;
  if (mode === 'cancel-probe') {
    console.log('CI cancellation probe ready');
    return child(state.node, ['-e', 'setTimeout(() => {}, 120000)']);
  }
  if (mode !== 'test') throw new Error('CI_TEST_UNKNOWN_PHASE');
  console.log('CI full suite starting; setup has completed.');
  return child(state.dotnet, ['test', 'src/Doctheca.sln', '--configuration', 'Release', '--no-build',
    '--logger', 'trx;LogFileName=doctheca-ut.trx', '--results-directory', 'reports']);
}
main().then(code => { process.exitCode = code; }).catch(error => {
  // Do not print subprocess environments or browser/network diagnostics.
  console.error(/^CI_TEST_[A-Z_]+$/.test(error.message) ? error.message : 'CI_TEST_CONTAINER_SETUP_FAILED'); process.exitCode = 1;
});
