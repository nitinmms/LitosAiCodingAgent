// Headless checks for the Litos Software Factory prototype (../litos-factory.html).
// Runs the simulation engine and renders every screen with React's server renderer, so a
// broken reducer, walkthrough step or template fails here instead of in the browser.
//   cd docs/software-factory/prototype/checks && npm install && npm run check
const fs = require('fs');
const path = require('path');
const assert = require('assert');
const React = require('react');
const { renderToString } = require('react-dom/server');
const htm = require('htm');

const src = fs.readFileSync(path.join(__dirname, '..', 'litos-factory.html'), 'utf8');
let js = src.slice(src.lastIndexOf('<script>') + 8, src.lastIndexOf('</script>'));
js = js.replace('window.__factoryTest={reducer,initialState,steps};',
  'window.__factoryTest={reducer,initialState,steps,Admin,Board,Threads,Lessons,TopBar,Login,Tour,NewThread,CatalogPicker,allowedModels,usableProviders};');
global.window = {};
global.React = React;
global.htm = htm;
global.ReactDOM = { createRoot: () => ({ render: () => {} }) };
global.document = { getElementById: () => null };
new Function(js)();

const X = window.__factoryTest;
const html = htm.bind(React.createElement);
const noop = () => {};
const render = (C, props) => renderToString(html`<${C} ...${Object.assign({ dispatch: noop }, props)}/>`);
let passed = 0;
const ok = (cond, msg) => { assert.ok(cond, msg); passed++; };

// 1. The guided walkthrough completes, rendering every view at every tick.
let S = X.initialState();
let renders = 0;
for (let guard = 0; guard < 300; guard++) {
  const list = X.steps(S);
  const cur = list.findIndex(s => !s.done);
  if (cur === -1) break;
  const step = list[cur];
  if (step.act.length && step.can !== false) for (const a of step.act) S = X.reducer(S, a);
  S = X.reducer(S, { type: 'TICK' });
  for (const V of [X.Threads, X.Board, X.Lessons, X.TopBar, X.Tour]) { render(V, { S }); renders++; }
}
ok(X.steps(S).every(s => s.done), 'walkthrough did not complete');
const csv = S.threads.find(t => t.script === 'csv');
ok(csv.state === 'Accepted' && csv.handoffs === 2 && csv.capRaised, 'CSV task should be accepted after rework and a budget raise');
ok(csv.used <= csv.cap, 'per-call reservation must keep usage within the cap');
ok(csv.messages.filter(m => m.kind === 'budget').length === 1, 'exactly one budget pause expected');
render(X.Login, {});

// 2. Every settings tab renders.
let A = X.reducer(X.initialState(), { type: 'SIGN_IN', user: 'priya' });
A = X.reducer(A, { type: 'REGISTER_PROJECT', data: { name: 'SalesApp', repo: 'https://github.com/harbor-tools/salesapp', branch: 'main', stack: 'x', coverage: '80', pr: true } });
for (const tab of ['projects', 'people', 'providers', 'mcp', 'skills', 'tools', 'limits', 'presets']) {
  A = X.reducer(A, { type: 'ADMIN_TAB', tab }); render(X.Admin, { S: A }); renders++;
}
render(X.CatalogPicker, { S: A, k: 'openrouter', close: noop });
render(X.NewThread, { S: A, close: noop });

// 3. Runs snapshot settings; later changes do not affect a running task.
A = X.reducer(A, { type: 'NEW_THREAD', data: { title: 'First', projectId: 'salesapp', type: 'Feature', cap: '50000' } });
const first = A.threads[0].id;
A = X.reducer(A, { type: 'SEND', id: first, text: '@factory go' });
ok(A.threads.find(t => t.id === first).snap.skipped.includes('salesapp-ui'), 'unapproved repository skill must be skipped');
A = X.reducer(A, { type: 'REPO_SKILL', projectId: 'salesapp', name: 'salesapp-ui', status: 'Approved' });
A = X.reducer(A, { type: 'TOGGLE_MCP', name: 'context7' });
ok(A.threads.find(t => t.id === first).snap.mcp.includes('context7'), 'running task must keep its snapshot');

// 4. Model catalog: retired models are withheld and the default falls back within the provider.
A = X.reducer(A, { type: 'SET_DEFAULT_MODEL', provider: 'openrouter', model: 'mistralai/codestral-2501' });
A = X.reducer(A, { type: 'REFRESH_CATALOG', id: 'openrouter' });
ok(!X.allowedModels(A, 'openrouter').includes('mistralai/codestral-2501'), 'retired model must be withheld from members');
ok(A.settings.defaultProvider === 'openrouter', 'default should fall back within the same provider');
A = X.reducer(A, { type: 'PROVIDER', id: 'anthropic', key: 'enabled', value: false });
ok(!X.usableProviders(A).includes('anthropic'), 'disabled provider must not be offered');
render(X.Admin, { S: A });

console.log(`OK: ${passed} assertions, ${renders} screen renders.`);
