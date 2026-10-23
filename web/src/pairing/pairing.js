// A QR code window: "Pair Phone" (the code the phone app scans to find this PC), and the
// Smart Life sign-in a paired iPhone asks this PC to show, because that code has to be scanned
// by the Smart Life app on the very phone that asked.
import './pairing.css';
import { h } from '../ui/dom.js';
import { icon } from '../ui/icons.js';
import { post, incoming } from '../bridge.js';

export function bootPairing(root) {
  let view = window.__HOUSE_BOOT__?.qr || sample();

  incoming.qrChanged = (next) => { view = next; render(); };

  function render() {
    const status = view.status
      ? h(`div.q-status${view.active ? '.on' : ''}`, h('i'), h('span', view.status))
      : null;
    root.replaceChildren(h('div.q-app',
      h('div.q-code', view.qr ? h('img', { src: view.qr, alt: 'QR code' }) : h('div.q-empty', icon('spinner', 28))),
      h('h1', view.headline),
      h('p.q-detail', view.detail),
      status,
      view.steps?.length ? h('ol.q-steps', view.steps.map((s) => h('li', s))) : null,
      view.buttons?.length ? h('div.q-buttons', view.buttons.map((b) => h(`button.q-btn${b.primary ? '.primary' : ''}`, {
        onclick: () => post(b.action),
      }, b.label))) : null,
      view.note ? h('p.q-note', view.note) : null));
  }

  render();
  post('ready');
}

// In a browser (vite dev): something to design against.
function sample() {
  return {
    qr: null,
    headline: 'Scan with House Control on your phone',
    detail: 'On the phone, Android or iPhone, open Settings → Pair with Mac or PC. Both devices must be on the same Wi-Fi.',
    status: 'Waiting for a phone…',
    active: false,
    steps: ['Open House Control on the phone.', 'Settings → Pair with Mac or PC.', 'Point the camera at this code.'],
    buttons: [{ label: 'Copy Pairing Link', action: 'pairing.copy' }, { label: 'Reset Pairing…', action: 'pairing.reset' }],
    note: 'Your floor plan, device links and connections stay the same on both.',
  };
}
