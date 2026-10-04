// Compile the current shared component and its real Icon dependency for browser lifecycle
// tests. Output is temporary and never shipped in the administration bundle.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse, compileScript } from '@vue/compiler-sfc'
import ts from 'typescript'
import sass from 'sass'

const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const output = process.argv[2]
if (!output) throw new Error('A temporary output directory is required.')
fs.mkdirSync(output, { recursive: true })
const source = fs.readFileSync(path.join(frontend, 'src/components/AppDrawer.vue'), 'utf8')
const descriptor = parse(source).descriptor
const component = compileScript(descriptor, { id: 'drawer-browser', inlineTemplate: true }).content
function module(source) {
  return ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
    .replace(/from ['"]vue['"]/g, 'from "/drawer-probe/vue.js"')
    .replace(/from ['"]\.\.\/utils\/icons['"]/g, 'from "/drawer-probe/icons.js"')
}
fs.writeFileSync(path.join(output, 'drawer.js'), module(component))
fs.writeFileSync(path.join(output, 'icons.js'), module(fs.readFileSync(path.join(frontend, 'src/utils/icons.ts'), 'utf8')))
fs.copyFileSync(path.join(frontend, 'node_modules/vue/dist/vue.esm-browser.js'), path.join(output, 'vue.js'))
const css = sass.compile(path.join(frontend, 'src/styles/components/_drawer.scss')).css
fs.writeFileSync(path.join(output, 'index.html'), `<!doctype html><style>${css}</style><div id="app"></div>
<script type="module">
import {createApp, ref, nextTick} from '/drawer-probe/vue.js';
import Drawer from '/drawer-probe/drawer.js';
const open=ref(new URL(location.href).searchParams.get('initial')==='true');
const mounted=ref(true);const clicks=ref(0);const closes=ref(0);
document.body.style.overflow='scroll';
window.control={async set(value){open.value=value;await nextTick()},async unmount(){mounted.value=false;await nextTick()},
async rapid(){open.value=true;await nextTick();open.value=false;await nextTick();open.value=true;await nextTick()},
get closes(){return closes.value}};
createApp({components:{Drawer},setup(){return {open,mounted,clicks,closes}},template:
'<button id="opener" @click="open=true">Open</button><button id="underlying" @click="clicks++">Page {{clicks}}</button><Drawer v-if="mounted" :open="open" title="Filter" @close="closes++;open=false"><input id="inside"><button id="inside-close" @click="open=false">Apply</button></Drawer>'}).mount('#app');
window.ready=true;
</script>`)
