# Changelog

## 1.1.5 — 2026-10-24

- **Pair Phone.** iPhones and Android phones pair with the PC alike, so the tray menu, the pairing window and
  Settings now say **Pair Phone…** and **Phone** instead of iPhone. On the phone the item is **Pair with Mac or PC**
  (**Pair with Mac** in versions before the rename); the pairing window names both. Pairing itself is unchanged:
  phones already paired keep syncing.
- **Test copies keep to themselves.** A copy started with its own `HOUSE_DATA_DIR` (tests, screenshots) is an
  instance of its own, so its arguments — a `--quit` included — no longer reach the copy you use every day.

## 1.1.4 — 2026-10-23

- **Quitting no longer crashes.** Once a window such as Settings, the editor or Pair iPhone had been open, quitting
  (or the installer updating the app) ended in a crash inside Windows' messaging. House Control now closes its
  windows, removes its tray icon and stops AI Access and sync, then ends cleanly.
- **Android phones sync too.** The House Control Android app pairs with the PC exactly as with a Mac (Settings →
  Pair with Mac, scan the PC's code); the PC is tested against the Android app's own connection settings.

## 1.1.3 — 2026-10-23

- **iPhone sync connects.** The iPhone's TLS asks for a certificate's revocation status (`status_request`) in every
  handshake; the PC agreed to send one, though a connection with a pre-shared key has no certificate, and the
  iPhone ended the handshake with `decode_error`. The PC now declines it, as a Mac does, and pairs with the iPhone
  app — checked with a real iPhone, whose home arrived on the PC.
- The app's log notes what a phone offers in its handshake, should one ever fail again.

## 1.1.2 — 2026-10-23

- **The iPhone completes the handshake.** The PC now sends the pre-shared key's identity hint, as a Mac does;
  without it the iPhone app's encrypted connection stopped with `decode_error`.
- **A VPN no longer hides the PC.** With NordVPN or another VPN connected, the PC announced its tunnel address,
  which a phone on the Wi-Fi cannot reach. The Wi-Fi or wired address now always comes first.

## 1.1.1 — 2026-10-23

- **Pair iPhone tells you what is in the way.** When Windows treats your Wi-Fi as a public network, the window says
  so and opens the setting that makes it private; when the firewall has no rule for House Control, **Allow in
  Firewall…** adds one for private networks after Windows' administrator prompt. It checks again by itself.
- Every connection a phone makes, and why one did not get through, is written to the app's log.
- A test of the sync server no longer fails now and then on GitHub's build machines.

## 1.1.0 — 2026-10-23

- **Sync with the iPhone app.** Pair House Control on your iPhone with your PC — tray icon → **Pair iPhone…** — and
  the floor plan, the device links and the Home Assistant and Hue connections stay the same on both, whichever one
  you edit. The iPhone sees the PC just like a Mac: no change to the iPhone app is needed. The phone's Apple Home
  devices can be linked in the editor, and its Smart Life sign-in code can be shown on the PC's screen.
- **A compact island.** Without a notch to match, the tab at the top of the screen is now only as wide as what it
  shows, and 20% see-through at rest. **Settings → Island** has its width (Compact, Medium, Wide) and opacity.
- The README's icon is the icon again.

## 1.0.0 — 2026-10-23

House Control comes to Windows: your home as a live 3D model, a click away in the taskbar.

- **The panel** slides up from the tray icon like Windows' own flyouts — the 3D model, scenes, every light and
  air conditioner — and opens from a pinned taskbar button right above it, or from anywhere with `Ctrl+Alt+H`.
- **The tray icon** lights up warm when lights are on and counts them in a badge; its menu has the scenes, all
  lights on or off, the island, the desktop widget, Start with Windows, AI Access and Settings.
- **A jump list** on the pinned taskbar button: your scenes, Turn all lights off, Edit home and Settings.
- **The island**: a small tab at the top of the screen that springs open into a live model of the home when the
  pointer rests on it, peeks out when something changes, lets every click through while closed and steps aside for
  full-screen apps. A slim-line style, the opening delay and the display are yours to choose.
- **A desktop widget**: the home on your desktop, under every window and still there after `Win+D`.
- **Settings** in the style of Windows 11's own: start with Windows, the shortcut, the island, the widget, plans,
  connections, AI Access.
- **Real devices** through Smart Life / Tuya, Home Assistant and Philips Hue, found on your network by themselves;
  sign-ins encrypted for your Windows account.
- **AI Access** for ChatGPT, Claude and other assistants, over a private link or MCP, served by your PC.
- **The editor** for rooms, furniture, lamps and air conditioners, with the plan format shared with the Mac app.
- A per-user installer (no administrator prompt) and a portable zip, for x64 and Arm64, built by GitHub Actions.
