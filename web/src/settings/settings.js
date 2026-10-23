// The Settings window: everything about House Control in one place, laid out like Windows'
// own Settings — the sections on the left, cards of settings on the right. The native side
// owns every value; this page shows them and asks for changes.
import './settings.css';
import { h } from '../ui/dom.js';
import { icon } from '../ui/icons.js';
import { isNative, post, call, incoming } from '../bridge.js';

const PANES = [
  { id: 'general', name: 'General', icon: 'settings' },
  { id: 'island', name: 'Island', icon: 'box' },
  { id: 'widget', name: 'Desktop widget', icon: 'laptop' },
  { id: 'home', name: 'Home', icon: 'home' },
  { id: 'connections', name: 'Connections', icon: 'link' },
  { id: 'ai', name: 'AI Access', icon: 'sparkles' },
  { id: 'shortcuts', name: 'Getting around', icon: 'key' },
  { id: 'about', name: 'About', icon: 'book' },
];

const SERVICES = [
  { id: 'smartlife', name: 'Smart Life', tint: '#ff8a3d', icon: 'plug', off: 'Not signed in', on: () => 'Signed in' },
  { id: 'homeassistant', name: 'Home Assistant', tint: '#41bdf5', icon: 'homeassistant', off: 'Not connected', on: (i) => i.name || 'Connected' },
  { id: 'hue', name: 'Philips Hue', tint: '#8f7bff', icon: 'bridge', off: 'Not paired', on: (i) => i.name || 'Connected' },
];

export function bootSettings(root) {
  const boot = window.__HOUSE_BOOT__ || {};
  let state = boot.settings || sample();
  let pane = boot.pane || new URLSearchParams(location.search).get('pane') || 'general';
  if (!PANES.some((p) => p.id === pane)) pane = 'general';

  const nav = h('nav.s-nav');
  const content = h('main.s-content');
  root.replaceChildren(h('div.s-app', h('aside.s-side',
    h('div.s-brand', h('span.s-logo', icon('home', 18, 2)), h('div', h('b', 'House Control'), h('small', `Version ${state.version}`))),
    nav), content));

  const set = (key, value) => {
    if (isNative) post('settings.set', { key, value });
    else { state = { ...state, [key]: value }; render(); }
  };

  incoming.settingsChanged = (next) => { state = next; render(); };
  incoming.showPane = (id) => { if (PANES.some((p) => p.id === id)) { pane = id; render(); } };

  window.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape' && !recording) window.close?.();
  });

  /* ---------- building blocks ---------- */

  function toggle(on, onChange, label) {
    const el = h('button.s-toggle', { role: 'switch', 'aria-checked': String(Boolean(on)), 'aria-label': label,
      onclick: () => onChange(!on) }, h('i'));
    el.classList.toggle('on', Boolean(on));
    return h('div.s-toggle-wrap', h('span.s-toggle-text', on ? 'On' : 'Off'), el);
  }

  function choice(value, options, onChange, label) {
    return h('div.s-segmented', { role: 'radiogroup', 'aria-label': label },
      options.map(([id, text]) => h(`button${id === value ? '.on' : ''}`, {
        role: 'radio', 'aria-checked': String(id === value), onclick: () => id !== value && onChange(id),
      }, text)));
  }

  function select(value, options, onChange, label) {
    const el = h('select.s-select', { 'aria-label': label, onchange: (e) => onChange(e.target.value) },
      options.map(([id, text]) => h('option', { value: id, selected: id === value }, text)));
    return el;
  }

  // A slider that says its value and only reports it once the thumb is let go.
  function range(value, min, max, step, onChange, label) {
    const out = h('span.s-range-value', `${value}%`);
    const input = h('input.s-range', { type: 'range', min, max, step, value, 'aria-label': label });
    input.addEventListener('input', () => { out.textContent = `${input.value}%`; });
    input.addEventListener('change', () => onChange(Number(input.value)));
    return h('div.s-inline', input, out);
  }

  function row(iconName, title, detail, control, extra) {
    return h('div.s-row', iconName ? h('span.s-row-icon', icon(iconName, 18)) : null,
      h('div.s-row-text', h('div.s-row-title', title), detail ? h('div.s-row-detail', detail) : null),
      control ? h('div.s-row-control', control) : null, extra);
  }

  function group(...rows) { return h('section.s-group', rows); }
  function button(text, onclick, primary = false) { return h(`button.s-btn${primary ? '.primary' : ''}`, { onclick }, text); }
  function linkButton(text, onclick) { return h('button.s-link', { onclick }, text, icon('external', 14, 2)); }

  /* ---------- panes ---------- */

  function general() {
    const startup = state.startWithWindows;
    return [
      group(
        row('power', 'Start with Windows', startup === 'disabled'
          ? 'Switched off in Task Manager → Startup apps. Turn it on here to allow it again.'
          : 'House Control waits in the tray from the moment you sign in.',
        toggle(startup === 'on', (on) => set('startWithWindows', on), 'Start with Windows')),
        row('key', 'Keyboard shortcut', state.hotkeyProblem || 'Opens and closes the panel from anywhere.',
          h('div.s-inline', hotkeyButton(), toggle(state.hotkeyEnabled, (on) => set('hotkeyEnabled', on), 'Keyboard shortcut'))),
        row('bulb', 'Count lights in the tray icon', 'A small badge on the house says how many lights are on.',
          toggle(state.trayBadge, (on) => set('trayBadge', on), 'Count lights in the tray icon')),
      ),
      h('h3.s-sub', 'From the taskbar'),
      group(
        row('home', 'Pin House Control to the taskbar', 'Right-click its icon in Start → Pin to taskbar. A click on the pinned button opens the panel right above it; a right-click lists your scenes and All lights off.'),
        row('list', 'Tray icon', 'Click it to open the panel, right-click it for scenes, the island, the widget and more. Drag it out of the overflow corner (˄) onto the taskbar to keep it in sight.'),
      ),
    ];
  }

  let recording = false;
  function hotkeyButton() {
    const el = h('button.s-key', { title: 'Click, then press the new shortcut' }, recording ? 'Press keys…' : state.hotkeyText);
    el.classList.toggle('recording', recording);
    el.onclick = () => { recording = true; render(); };
    return el;
  }

  window.addEventListener('keydown', async (ev) => {
    if (!recording) return;
    ev.preventDefault();
    if (ev.key === 'Escape') { recording = false; render(); return; }
    if (['Control', 'Alt', 'Shift', 'Meta'].includes(ev.key)) return;
    const modifiers = (ev.altKey ? 1 : 0) | (ev.ctrlKey ? 2 : 0) | (ev.shiftKey ? 4 : 0) | (ev.metaKey ? 8 : 0);
    const key = ev.keyCode;
    recording = false;
    if (!isNative) { state = { ...state, hotkeyText: describeKeys(ev) }; render(); return; }
    try {
      await call('settings.hotkey', { modifiers, key });
    } catch (e) {
      state = { ...state, hotkeyProblem: e.message };
      render();
    }
  });

  function island() {
    const on = state.islandEnabled;
    const displays = state.displays || [];
    return [
      h('p.s-lead', 'A small black tab hangs from the top edge of your screen. Rest the pointer on it and it opens into a live 3D model of your home, with scenes and every device one click away. When something changes elsewhere, it peeks out to say so.'),
      group(row('box', 'Show the island', 'It never takes the keyboard from the app you are working in, and lets every click through while it is closed.',
        toggle(on, (v) => set('islandEnabled', v), 'Show the island'))),
      h('div.s-dim', { class: on ? 's-dim' : 's-dim off' },
        group(
          row('fit', 'At rest', 'A tab with how many lights are on, or only a thin line until the pointer comes.',
            choice(state.islandRest, [['tab', 'Tab'], ['slim', 'Slim line']], (v) => set('islandRest', v), 'At rest')),
          row('fit', 'Width', 'Compact is only as wide as what it shows; Wide matches a MacBook’s notch.',
            choice(state.islandWidth || 'compact', [['compact', 'Compact'], ['medium', 'Medium'], ['wide', 'Wide']], (v) => set('islandWidth', v), 'Width')),
          row('dim', 'Opacity', 'How solid its black is while it rests or peeks, so you can see what is under it.',
            range(state.islandOpacity ?? 80, 20, 100, 5, (v) => set('islandOpacity', v), 'Opacity')),
          row('clock', 'Opens after', 'How long the pointer rests on it first, so passing by does nothing.',
            choice(String(delayBucket(state.islandDelay)), [['120', 'Quickly'], ['220', 'Normal'], ['450', 'Slowly']],
              (v) => set('islandDelay', Number(v)), 'Opens after')),
          row('activity', 'Peek when something changes', 'A lamp switched from your phone, a scene picked in the panel: it shows what happened for a moment.',
            toggle(state.islandPeek, (v) => set('islandPeek', v), 'Peek when something changes')),
          row('film', 'Hide while an app is full screen', 'Steps aside for games, videos and presentations, the way notifications do.',
            toggle(state.islandHideInFullscreen, (v) => set('islandHideInFullscreen', v), 'Hide while an app is full screen')),
          displays.length > 1
            ? row('laptop', 'Display', 'Which screen it lives on.', select(state.islandDisplay, displays.map((d) => [d.id, d.name]), (v) => set('islandDisplay', v), 'Display'))
            : null,
        )),
    ];
  }

  function delayBucket(ms) { return ms <= 160 ? 120 : ms <= 320 ? 220 : 450; }

  function widget() {
    const on = state.widgetEnabled;
    return [
      h('p.s-lead', 'Your home on the desktop, under every window: the 3D model, the scenes and the devices, live. Drag it by its title to put it where you like.'),
      group(
        row('laptop', 'Show on the desktop', null, toggle(on, (v) => set('widgetEnabled', v), 'Show on the desktop')),
        row('shield', 'Lock its position', 'It stays where it is, and its title no longer moves it.',
          toggle(state.widgetLocked, (v) => set('widgetLocked', v), 'Lock its position')),
        row('rotate', 'Put it back in the corner', 'The top right of the main display.', button('Reset position', () => post('settings.resetWidget'))),
      ),
    ];
  }

  function home() {
    const plans = state.plans || [];
    return [
      group(
        row('map', 'Floor plan', 'Your own home, or one of the two demo plans to try things with.',
          select(state.activePlan, plans.map((p) => [p.id, p.name]), (v) => set('plan', v), 'Floor plan')),
        row('home', 'Edit home', 'Draw rooms, add furniture, place lamps and air conditioners, link them to real devices.',
          button('Open editor', () => post('openEditor'), true)),
        row('folder', 'Load a floor plan', 'Use a plan file someone shared, or one you wrote by hand.', button('Choose file…', () => post('loadPlan'))),
        row('list', 'Plan file', 'Where your plan is kept.', button('Show in Explorer', () => post('revealPlan'))),
        row('activity', 'Device log', 'What was sent to real devices and what they answered. The first place to look when a device does not react.',
          button('Show in Explorer', () => post('revealLog'))),
      ),
    ];
  }

  function connections() {
    const services = state.services || {};
    return [
      h('p.s-lead', 'Connect the platforms your lights and air conditioners live on, then link each lamp and AC of the plan to its real device in the editor.'),
      group(...SERVICES.map((s) => {
        const info = services[s.id] || {};
        const connected = Boolean(info.connected);
        return row(null, h('span.s-service', h('span.s-service-icon', { style: { background: s.tint } }, icon(s.icon, 16, 2)), s.name),
          h('span', { class: connected ? 's-ok' : '' }, connected ? s.on(info) : s.off),
          button(connected ? 'Manage…' : 'Connect…', () => post('openEditor', { section: `service:${s.id}` }), !connected));
      })),
      h('p.s-note', 'Smart Life is reached through Tuya\'s cloud; Home Assistant and Hue work on your own network. Sign-ins stay on this PC, encrypted for your Windows account.'),
    ];
  }

  function ai() {
    return [
      h('p.s-lead', 'Give ChatGPT, Claude or any AI assistant a private link — or connect it over MCP — and it can run your lights and air conditioners, and rebuild rooms in the 3D model from your photos. Served by this PC; Stop ends it.'),
      group(row('sparkles', 'AI Access', state.aiLive ? 'Live: an assistant can use the link now.' : 'Off',
        h('div.s-inline', state.aiLive ? button('Stop', () => post('stopAgent')) : null, button(state.aiLive ? 'Open…' : 'Set up…', () => post('openAgent'), !state.aiLive)))),
    ];
  }

  function shortcuts() {
    const rows = [
      [state.hotkeyText, 'Open or close the panel, from anywhere'],
      ['Click the tray icon', 'Open or close the panel'],
      ['Right-click the tray icon', 'Scenes, all lights, the island, the widget, Settings'],
      ['Right-click the taskbar button', 'Scenes and the everyday commands (once pinned)'],
      ['Click a lamp in 3D, or its tile', 'Switch it on or off'],
      ['Drag sideways across a light tile', 'Dim it'],
      ['Click an AC, or its tile', 'Temperature, mode and fan speed'],
      ['Click a room', 'Zoom in on it and list only its devices'],
      ['Drag the 3D view / scroll over it', 'Rotate / zoom'],
      ['Esc', 'Back, then close'],
      ['Ctrl+,', 'Settings'],
      ['Ctrl+Z / Ctrl+Y, Ctrl+S', 'Undo / redo and save in the editor'],
    ];
    return [group(...rows.map(([keys, what]) => h('div.s-row.s-keyrow', h('kbd', keys), h('span', what))))];
  }

  function about() {
    return [
      h('div.s-about', h('span.s-logo.big', icon('home', 34, 1.8)),
        h('div', h('h2', 'House Control'), h('p', `Version ${state.version} for Windows`),
          h('p.s-muted', 'Your home as a live 3D model, in the taskbar, at the top of your screen and on your desktop.'))),
      group(
        row('book', 'Source code', 'Open source under the MIT license.', linkButton('GitHub', () => post('openLink', { url: 'https://github.com/0xProbabillity/house-control-windows' }))),
        row('alert', 'Report a problem', 'Bugs and ideas are welcome.', linkButton('Issues', () => post('openLink', { url: 'https://github.com/0xProbabillity/house-control-windows/issues' }))),
        row('phone', 'House Control for Mac and iPhone', 'The same home in the Mac\'s menu bar and on the iPhone.', linkButton('Learn more', () => post('openLink', { url: 'https://github.com/0xProbabillity/house-control' }))),
        row('folder', 'Your data', state.dataDirectory || '%APPDATA%\\HouseControl', null),
      ),
      h('p.s-note', 'Made by 0xProbability. Not affiliated with Tuya, Smart Life, Home Assistant or Signify (Philips Hue).'),
    ];
  }

  const BUILD = { general, island, widget, home, connections, ai, shortcuts, about };

  function render() {
    nav.replaceChildren(...PANES.map((p) => h(`button.s-nav-item${p.id === pane ? '.on' : ''}`, {
      onclick: () => { pane = p.id; render(); content.scrollTop = 0; },
      'aria-current': p.id === pane ? 'page' : null,
    }, icon(p.icon, 17), h('span', p.name))));
    const current = PANES.find((p) => p.id === pane);
    content.replaceChildren(h('h1', current.name), ...BUILD[pane]());
  }

  render();
  post('ready');
}

function describeKeys(ev) {
  return [ev.ctrlKey && 'Ctrl', ev.altKey && 'Alt', ev.shiftKey && 'Shift', ev.metaKey && 'Win', ev.key.length === 1 ? ev.key.toUpperCase() : ev.key]
    .filter(Boolean).join('+');
}

// In a browser (vite dev) there is no native side: a plausible state, so the page can be designed.
function sample() {
  return {
    version: '1.0.0', startWithWindows: 'on', hotkeyEnabled: true, hotkeyText: 'Ctrl+Alt+H', hotkeyProblem: '', trayBadge: true,
    islandEnabled: true, islandRest: 'tab', islandWidth: 'compact', islandOpacity: 80, islandDelay: 220, islandPeek: true, islandHideInFullscreen: true, islandDisplay: '',
    displays: [{ id: '', name: 'Display 1 (main) · 1920×1080' }, { id: 'B', name: 'Display 2 · 2560×1440' }],
    widgetEnabled: false, widgetLocked: false,
    plans: [{ id: 'home', name: 'Demo · Apartment' }, { id: 'studio', name: 'Demo · Studio' }], activePlan: 'home',
    services: { smartlife: { connected: false }, homeassistant: { connected: true, name: 'Home' }, hue: { connected: false } },
    aiLive: false, dataDirectory: '%APPDATA%\\HouseControl',
  };
}
