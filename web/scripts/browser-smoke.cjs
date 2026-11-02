// Requires the Vite dev server and backend. All fixtures are synthetic; no game assets are read or written.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const { existsSync } = require('node:fs');
const baseURL = process.env.MAP_BUILDER_URL || 'http://127.0.0.1:5173/';

async function checkRendering(browser) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  page.setDefaultTimeout(20000);
  try {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(baseURL);
  await page.getByLabel('Render resolution').selectOption('0.5');
  await page.getByLabel('Texture filtering').selectOption('pixel');
  await page.getByLabel('Statistics', { exact: true }).check();
  await page.getByLabel('Viewport statistics').waitFor();
  await page.reload();
  await page.getByLabel('Render resolution').waitFor();
  assert.equal(await page.getByLabel('Render resolution').inputValue(), '0.5');
  assert.equal(await page.getByLabel('Texture filtering').inputValue(), 'pixel');
  assert.equal(await page.getByLabel('Statistics', { exact: true }).isChecked(), true);
  const layout = await page.locator('.me-app.mp-app .me-center').boundingBox();
  assert(layout.height > 300, 'Viewport must retain usable height');
  const result = await page.evaluate(async () => {
    const { MapViewport } = await import('/src/viewport/mapViewport.ts');
    const host = document.createElement('div');
    Object.assign(host.style, { position: 'fixed', top: '0', left: '0', width: '800px', height: '600px', zIndex: '10000' });
    const canvas = document.createElement('canvas'); host.append(canvas); document.body.append(host);
    // Synthetic PointerEvents have no active device pointer for native capture.
    canvas.setPointerCapture = () => {}; canvas.releasePointerCapture = () => {};
    let committed = null;
    const vp = new MapViewport(canvas, { onPlace: transform => { committed = transform; } });
    const chunk = (positions, triangles, normals) => ({ index: 0, geometryOffset: 0, visual: null, texture: null, materialBlend: false, unit: '', vertexCount: positions.length / 3,
      positions: new Float32Array(positions), triangles: new Uint32Array(triangles), normals: new Float32Array(normals), uvs: new Float32Array(positions.length / 3 * 2), colors: null });
    const ground = { archive: 'A_MDL.ZED', name: 'WORLD', revision: 0, skeleton: null, skinInfluences: 0,
      chunks: [chunk([-100,0,-100, 100,0,-100, 100,0,100, -100,0,100], [0,2,1, 0,3,2], [0,1,0, 0,1,0, 0,1,0, 0,1,0])] };
    const prop = { ...ground, name: 'PROP', chunks: [chunk([-2,0,0, 2,0,0, 0,8,0], [0,1,2], [0,0,1,0,0,1,0,0,1])] };
    const identity = [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
    vp.setScene([{ kind: 'model', model: 'WORLD', instance: 'world', path: '/world', position: [0,0,0], scale: 1, matrix: identity, geoIndex: null, clutterIndex: null, source: 'A_MDL.ZED:WORLD' }],
      new Map([['A_MDL.ZED:WORLD', { mesh: ground, textureNamespace: null }]]), {}, [], false);
    vp.setRenderSettings({ resolution: 0.5, filtering: 'pixel', showStats: false });
    vp.setPlacementMesh(prop);
    vp.setPlacementOptions({ yaw: 30, scale: 1.5, randomYaw: true, randomScale: true, minScale: 0.8, maxScale: 1.2, alignSurface: true });
    vp.tool = 'place';
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    canvas.dispatchEvent(new PointerEvent('pointermove', { clientX: 400, clientY: 300, bubbles: true }));
    const ghostVisible = vp.placementGhost.visible;
    const preview = vp.placementGhost.matrix.toArray();
    canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: 400, clientY: 300, button: 0, bubbles: true }));
    const exactPreview = JSON.stringify(preview) === JSON.stringify(committed?.matrix);
    vp.setLayerLocked('clutter', true);
    committed = null;
    canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: 400, clientY: 300, button: 0, bubbles: true }));
    const lockedBlocked = !vp.placementGhost.visible && committed === null;
    vp.setLayerLocked('clutter', false);
    vp.editable = false;
    canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: 400, clientY: 300, button: 0, bubbles: true }));
    const readOnlyBlocked = !vp.placementGhost.visible && committed === null;
    vp.editable = true;
    vp.tool = 'select';
    const cancelled = !vp.placementGhost.visible;
    const pixelRatio = vp.renderer.getPixelRatio();
    const mixed = ['model', 'reference', 'clutter'].map((kind, index) => {
      const matrix = [...identity]; matrix[12] = index * 1000;
      return { kind, model: 'WORLD', instance: `instance${index}`, path: `/instance${index}`, position: [index * 1000, 0, 0], scale: 1, matrix,
        geoIndex: kind === 'reference' ? 1 : null, clutterIndex: kind === 'clutter' ? 0 : null, source: 'A_MDL.ZED:WORLD' };
    });
    const models = new Map([['A_MDL.ZED:WORLD', { mesh: ground, textureNamespace: null }]]);
    const pixels = {};
    const points = [{ name: 'start', kind: 'start', team: 0, x: 0, z: 0 }];
    vp.setScene(mixed, models, pixels, points, false);
    vp.setLayerHidden('clutter', true);
    const layersIndependent = vp.content.children.length === 3 && vp.content.children.every(mesh => mesh.visible === (mesh.userData.layer !== 'clutter'));
    vp.setLayerLocked('reference', true); vp.select([1, 99]);
    const lockedSelectionBlocked = vp.selected.size === 0;
    vp.setLayerLocked('reference', false); vp.select([1]);
    const unlockedSelectionWorks = vp.selected.has(1);
    vp.setLayerLocked('points', true); vp.selectPoint('start');
    const lockedPointBlocked = vp.selectedPoint === null;
    vp.flySpeed = 123;
    const moved = mixed.map(instance => ({ ...instance, matrix: [...instance.matrix] }));
    moved[1].matrix[12] = 6000;
    const originalMesh = vp.content.children[0];
    vp.setScene(moved, models, pixels, points, true);
    vp.frameAll();
    const boundsUpdated = Math.abs(vp.worldBox.max.x - 6100) < 0.001 && Math.abs(vp.controls.target.x - 3000) < 0.001;
    const fastUpdatePreserved = originalMesh === vp.content.children[0] && vp.flySpeed === 123;
    vp.dispose();
    const overlayRemoved = !host.querySelector('.me-boxselect');
    const { EditorViewport } = await import('/src/webview/model-editor/editor-viewport.ts');
    const editor = new EditorViewport(canvas);
    editor.setMesh(prop, {});
    editor.selectedChunks.add(0);
    editor.refreshOverlays();
    const overlayMaterial = editor.overlayGroup.children[0].material;
    let overlayDisposed = false;
    overlayMaterial.addEventListener('dispose', () => { overlayDisposed = true; });
    editor.refreshOverlays();
    let brushDisposed = false;
    editor.brush.material.addEventListener('dispose', () => { brushDisposed = true; });
    editor.dispose();
    const editorCleanup = overlayDisposed && brushDisposed && !host.querySelector('.me-boxselect');
    host.remove();
    return { editorCleanup, layersIndependent, lockedSelectionBlocked, unlockedSelectionWorks, lockedPointBlocked, boundsUpdated, fastUpdatePreserved, ghostVisible, exactPreview, lockedBlocked, readOnlyBlocked, cancelled, overlayRemoved, pixelRatio };
  });
  for (const [name, value] of Object.entries(result)) assert(name === 'pixelRatio' ? value === 1 : value === true, `${name}: ${value}`);
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ preferencesPersisted: true, statsVisible: true, viewportHeight: layout.height, ...result, pageErrors: errors }));

  } finally { await page.close(); }
}

async function checkWorkflow(browser) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 850 } });
    page.setDefaultTimeout(20000);
    try {
    const errors=[]; page.on('pageerror', error => errors.push(error.message));
    await page.goto(baseURL);
    const map=page.locator('.me-app.mp-app');
    await map.getByRole('button', { name: 'Studio', exact: true }).click();
    await map.getByLabel('Grid', { exact:true }).uncheck();
    await map.getByLabel('Snap', { exact:true }).check();
    await page.getByTitle('Move snap (units)', { exact:true }).fill('20');
    await page.getByTitle('Move snap (units)', { exact:true }).press('Enter');
    await map.getByRole('button', {name:'Ortho', exact:true}).click();
    await map.getByRole('button', {name:'Wire', exact:true}).click();
    await map.getByRole('button', {name:'Place', exact:true}).click();
    await map.getByLabel('Scale', {exact:true}).fill('1.25');
    await map.getByLabel('Scale', {exact:true}).press('Enter');
    await map.getByLabel('Random scale', {exact:true}).check();
    await map.getByLabel('Align to surface', {exact:true}).check();
    await page.reload();
    assert((await map.getByRole('button', { name: 'Studio', exact: true }).getAttribute('class')).includes('on'));
    assert(!(await map.getByLabel('Grid', {exact:true}).isChecked()));
    assert.equal(await page.getByTitle('Move snap (units)', {exact:true}).inputValue(),'20');
    assert((await map.getByRole('button', {name:'Ortho',exact:true}).getAttribute('class')).includes('on'));
    assert((await map.getByRole('button', {name:'Wire',exact:true}).getAttribute('class')).includes('on'));
    await map.getByRole('button', {name:'Place',exact:true}).click();
    assert.equal(await map.getByLabel('Scale', {exact:true}).inputValue(),'1.25');
    assert(await map.getByLabel('Random scale', {exact:true}).isChecked());
    assert(await map.getByLabel('Align to surface', {exact:true}).isChecked());
    console.log('View, snapping and placement preferences persist; precise numeric inputs commit');
    await map.getByRole('button', {name:'Help', exact:true}).click();
    await map.getByRole('button', {name:'Keyboard shortcuts…',exact:false}).click();
    const dialog=map.getByRole('dialog',{name:'Keyboard shortcuts'});
    await dialog.waitFor();
    await map.getByLabel('Search shortcuts').fill('editing redo');
    assert.equal(await dialog.locator('tbody tr').count(),1);
    assert((await dialog.locator('tbody').innerText()).includes('Redo'));
    await map.getByLabel('Search shortcuts').fill('nothingmatches');
    assert(await dialog.getByRole('status').isVisible());
    await page.keyboard.press('Escape');
    await dialog.waitFor({state:'hidden'});
    await page.keyboard.press('?');
    await dialog.waitFor();
    await dialog.getByRole('button',{name:'Close',exact:true}).click();
    console.log('Shortcut search, empty state, Escape and ? verified');
    let current='before.zdb', phase='initial', revision=0, editCalls=0;
    let holdNextScene = false, sceneHeld = false, sceneOverlap = false, releaseScene, markHeld;
    const held = new Promise(resolve => { markHeld = resolve; });
    const identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
    const status=()=>({session:current,path:current,isZdb:true,mapNamespace:'SYN',revision,dirty:[],canUndo:null,canRedo:null,entries:1,models:{}});
    await page.route('**/api/command', async route=>{
      const {Command,InputPath}=route.request().postDataJSON();
      let data={};
      if(Command==='ASSET_OPEN'){current=InputPath;data={status:status()};}
      else if(Command==='ASSET_STATUS') data=status();
      else if(Command==='ASSET_EDIT') { editCalls++; data={revision}; }
      else if(Command==='ASSET_LIST_MODELS') data=[];
      else if(Command==='ASSET_MAP_SCENE'){
        if (sceneHeld) { sceneOverlap = true; }
        if (holdNextScene) { holdNextScene = false; sceneHeld = true; markHeld(); await new Promise(resolve => { releaseScene = resolve; }); sceneHeld = false; }
        if(phase==='fail') return route.fulfill({json:{Success:false,Error:'Synthetic scene unavailable'}});
        data={sceneNamespace:'SYN',counts:{reference:1},instances:[{kind:'reference',model:current==='before.zdb'?'BEFORE':'AFTER',instance:'prop',path:'/prop',position:[0,0,0],scale:1,matrix:identity,geoIndex:1,clutterIndex:null,source:null}]};
      } else if(Command==='ASSET_MAP_POINTS'){
        if(phase==='points') return route.fulfill({json:{Success:false,Error:'Synthetic points unavailable'}});
        data={points:[]};
      }
      await route.fulfill({json:{Success:true,Data:JSON.stringify(data)}});
    });
    const open=path=>page.evaluate(async path=>{const {host}=await import('/src/shared/host.ts');host.open('map',path);},path);
    await open('before.zdb');
    await page.locator('.me-app.mp-app .me-vp-label').filter({hasText:'1 instances'}).waitFor();
    phase='fail';
    await open('after.zdb');
    await map.getByRole('button',{name:'Retry map loading'}).waitFor();
    assert.equal(await page.locator('.me-app.mp-app .me-vp-label').filter({hasText:'1 instances'}).count(),0);
    assert.equal(await page.locator('.me-app.mp-app .mp-progress').count(),0);
    phase='points';
    await map.getByRole('button',{name:'Retry map loading'}).click();
    await page.locator('.me-app.mp-app .me-vp-label').filter({hasText:'1 instances'}).waitFor();
    await page.locator('.mp-load-notice summary').click();
    await page.getByText('AIMAPS points could not be loaded:',{exact:false}).waitFor();
    phase='good';
    await map.getByRole('button',{name:'Retry map loading'}).click();
    await page.locator('.mp-load-notice').waitFor({state:'hidden'});
    console.log('Failed package scene clears old map; retry recovers; partial point failures stay visible until successful retry');
    await map.locator('.mp-model > span[title]').filter({hasText:'AFTER'}).click();
    phase='fail'; revision=1;
    await map.getByRole('button',{name:'Retry map loading'}).waitFor();
    await page.keyboard.press('Delete');
    await map.locator('.me-statusbar').filter({hasText:'Reload the map before editing'}).waitFor();
    assert.equal(editCalls,0);
    phase='good';
    await map.getByRole('button',{name:'Retry map loading'}).click();
    await page.locator('.mp-load-notice').waitFor({state:'hidden'});
    console.log('A failed same-session reload blocks stale edits until recovery');
    holdNextScene = true;
    await map.getByRole('button', { name: 'File', exact: true }).click();
    await map.getByRole('button', { name: 'Reload', exact: true }).click();
    await Promise.race([held, new Promise((_, reject) => setTimeout(() => reject(new Error('Reload did not reach scene request')), 10000))]);
    await page.evaluate(async () => {
      const { aiChange } = await import('/src/shared/aiBridge.ts');
      aiChange.set({ seq: 999, page: 'map', path: 'after.zdb', edited: true, description: 'queued follow-up' });
    });
    releaseScene();
    await map.locator('.me-statusbar').filter({ hasText: 'AI: queued follow-up' }).waitFor();
    assert.equal(sceneOverlap, false);
    console.log('An AI refresh received during loading waits until the current operation finishes');

    assert.deepEqual(errors,[]);
    console.log('Browser page errors: 0');

    } finally { await page.close(); }
}

async function checkEditorLoading(browser) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 850 } });
  page.setDefaultTimeout(20000);
  const errors = [];
  const requests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => requests.push(request.url()));
  try {
    await page.goto(baseURL);
    const navigation = page.getByRole('navigation', { name: 'Editors' });
    const map = page.locator('[data-editor="map"]');
    await map.locator('canvas').waitFor();
    assert.equal(await page.locator('[data-editor]').count(), 1);
    assert(!requests.some(url => /\/(ModelEditorPage|AnimationEditorPage|ViewerPage)\.tsx/.test(url)));
    await map.locator('canvas').evaluate(canvas => { canvas.dataset.smokeIdentity = 'original-map'; });
    await navigation.getByRole('button', { name: 'Model Editor', exact: true }).click();
    const model = page.locator('[data-editor="models"]');
    await model.locator('canvas').first().waitFor();
    await model.locator('canvas').first().evaluate(canvas => { canvas.dataset.smokeIdentity = 'original-model'; });
    await navigation.getByRole('button', { name: 'Animation Editor', exact: true }).click();
    await page.locator('[data-editor="animations"] canvas').waitFor();
    await navigation.getByRole('button', { name: 'Asset Viewer', exact: true }).click();
    await page.locator('[data-editor="viewer"] .zip-shell').waitFor();
    await navigation.getByRole('button', { name: 'Model Editor', exact: true }).click();
    assert.equal(await model.locator('canvas').first().getAttribute('data-smoke-identity'), 'original-model');
    await navigation.getByRole('button', { name: 'Map Builder', exact: true }).click();
    assert.equal(await map.locator('canvas').getAttribute('data-smoke-identity'), 'original-map');
    assert.equal(await page.locator('[data-editor]').count(), 4);
    assert.deepEqual(errors, []);
    console.log('Editors load only on first visit and retain their original viewports after switching');
  } finally { await page.close(); }

  const failed = await browser.newPage({ viewport: { width: 1280, height: 850 } });
  failed.setDefaultTimeout(20000);
  try {
    await failed.route('**/src/webview/model-editor/ModelEditorPage.tsx*', route => route.abort());
    await failed.goto(baseURL);
    const navigation = failed.getByRole('navigation', { name: 'Editors' });
    await navigation.getByRole('button', { name: 'Model Editor', exact: true }).click();
    await failed.getByRole('alert').filter({ hasText: 'Model Editor could not start' }).waitFor();
    await navigation.getByRole('button', { name: 'Map Builder', exact: true }).click();
    await failed.locator('[data-editor="map"] canvas').waitFor();
    console.log('An editor download failure leaves Map Builder available');
  } finally { await failed.close(); }
}

async function checkMcpBridge(browser) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 850 } });
  page.setDefaultTimeout(20000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let id = 0;
  const endpoint = process.env.MAP_BUILDER_MCP_URL || 'http://127.0.0.1:8770/mcp';
  const rpc = async (method, params = {}) => {
    const response = await fetch(endpoint, {
      method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
      body: JSON.stringify({ jsonrpc: '2.0', id: ++id, method, params }), signal: AbortSignal.timeout(20000),
    });
    assert(response.ok, `MCP HTTP ${response.status}`);
    const reply = await response.json();
    assert(!reply.error, JSON.stringify(reply.error));
    assert(!reply.result?.isError, JSON.stringify(reply.result));
    return reply.result;
  };
  try {
    await page.goto(baseURL);
    await page.waitForFunction(async () => (await import('/src/shared/aiBridge.ts')).aiConnected());
    const initial = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'browser-smoke', version: '1' } });
    assert.equal(initial.serverInfo.name, 'mapbuilder');
    const tools = await rpc('tools/list');
    assert(tools.tools.some(tool => tool.name === 'ui_view'));
    await rpc('tools/call', { name: 'ui_view', arguments: { lighting: 'studio', backdrop: 'blueprint', reason: 'browser smoke check' } });
    await page.waitForFunction(() => document.body.classList.contains('bg-blueprint')
      && [...document.querySelectorAll('.mp-app .me-seg button.on')].some(button => button.textContent === 'Studio'));
    const state = await rpc('tools/call', { name: 'ui_state', arguments: { reason: 'browser smoke check' } });
    const data = JSON.parse(state.content.find(content => content.type === 'text').text);
    assert.equal(data.state.pages.map.lighting, 'studio');
    assert.equal(data.state.backdrop, 'blueprint');
    const screenshot = await rpc('tools/call', { name: 'ui_screenshot', arguments: { maxSize: 320, reason: 'browser smoke check' } });
    assert(screenshot.content.some(content => content.type === 'image' && content.data.length > 100));
    await rpc('tools/call', { name: 'ui_view', arguments: { lighting: 'unlit', backdrop: 'dark', reason: 'browser smoke check' } });
    await page.waitForFunction(() => !document.body.classList.contains('bg-blueprint')
      && [...document.querySelectorAll('.mp-app .me-seg button.on')].some(button => button.textContent === 'Unlit'));
    await page.reload();
    assert((await page.locator('.mp-app').getByRole('button', { name: 'Unlit', exact: true }).getAttribute('class')).includes('on'));
    assert.deepEqual(errors, []);
    console.log('MCP initialize, tools, view events, UI state and screenshot bridge work with persistent lighting and backdrops');
  } finally { await page.close(); }
}

(async () => {
  const response = await fetch(new URL('/api/health', baseURL));
  assert(response.ok && (await response.json()).ok, 'Start the backend and Vite proxy before running browser checks');
  const executablePath = process.env.CHROMIUM_PATH || (existsSync('/usr/bin/chromium') ? '/usr/bin/chromium' : undefined);
  const browser = await chromium.launch({ executablePath, headless: true,
    args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  try {
    await checkRendering(browser);
    await checkWorkflow(browser);
    await checkEditorLoading(browser);
    await checkMcpBridge(browser);
    console.log('All Map Builder browser checks passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
