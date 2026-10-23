# Connecting real devices

House Control links the lamps and air conditioners of your floor plan to real devices on three platforms.
One home can mix them: Hue lamps, a Home Assistant air conditioner and a Smart Life plug side by side.
Everything you do not link stays simulated.

| Platform | How it connects | What you need |
| --- | --- | --- |
| **Smart Life / Tuya** | Tuya's cloud, signed in with a QR code | The Smart Life or Tuya Smart app — see [smart-life.md](smart-life.md) |
| **Home Assistant** | Your server's API, on your network | A long-lived access token |
| **Philips Hue** | The Hue Bridge's own API, on your network | Press the bridge's button once |

All three are set up in the editor: right-click the tray icon → **Edit Home… → Connections** (or **Settings →
Connections**). Then select a lamp or an AC on the plan, choose its device under **Real device**, and press
**Save & apply**. Picking a lamp fills in what it can do (dimming, white temperature, colour); picking an AC takes
over its temperature range.

## Home Assistant

[Home Assistant](https://www.home-assistant.io) runs your home locally and speaks to thousands of brands —
Zigbee and Z-Wave sticks, Matter, Google Nest, Xiaomi, Aqara, IKEA, Sonoff, SmartThings, Daikin, Mitsubishi,
Tuya — so connecting it brings all of them in at once.

1. In Home Assistant, open your profile (your name at the bottom left) → **Security** → **Long-lived access
   tokens** → **Create token**. Name it "House Control" and copy it.
2. In House Control: **Connections → Home Assistant**. Servers on your network are found by themselves (House
   Control asks the network over multicast DNS, the way Bonjour does) — press **Use**, or type the address
   (`http://homeassistant.local:8123`). Paste the token and press **Connect**.

| Home Assistant | In House Control |
| --- | --- |
| `light.*` | A lamp: on/off, brightness, white temperature and colour as its colour modes allow |
| `switch.*` | A lamp on a plug: on/off |
| `climate.*` | An air conditioner: power, target, the modes it has (`cool`, `heat`, `dry`, `fan_only`, `auto`/`heat_cool`), its fan speeds, swing, `eco` and `sleep` presets |
| `sensor.*` with `device_class: temperature` | A thermometer for an AC's room |

Areas become the room shown next to each device. House Control subscribes to Home Assistant's events, so a
lamp switched from a wall switch or an automation shows up in the panel within a second.

**Bring your Tuya devices in without a developer account:** in Home Assistant, **Settings → Devices & services →
Add integration → Tuya**, and sign in with the User Code and QR code from the Smart Life app — the same sign-in
House Control uses.

If Windows asks whether House Control may use private networks the first time it looks for servers, allow it:
that is how it finds Home Assistant and Hue Bridges on your Wi-Fi.

## Philips Hue

1. In House Control: **Connections → Philips Hue**. Bridges on your network are found by themselves (or type the
   address shown in the Hue app under **Settings → My Hue system**).
2. Press **Pair**, then the round button on top of the bridge within a minute.

Lamps, Hue Play bars, light strips and smart plugs appear with what each can do; the thermometers inside Hue
motion sensors can serve as an AC's room temperature. The bridge's event stream makes changes from the Hue app
show up at once. No Hue account or internet connection is involved.

The bridge's certificate is self-signed. House Control trusts it the first time and remembers its fingerprint,
so a different device answering at that address later is refused.

## Apple Home

Apple offers HomeKit to Apple devices only, so House Control for Windows cannot reach Apple Home accessories
directly. Two routes that work:

- **Home Assistant's HomeKit Controller** integration adds HomeKit and Matter accessories to Home Assistant, and
  from there they appear in House Control.
- **Matter**: Matter accessories can be shared with several apps at once ("multi-admin"). In the Apple Home app,
  open the accessory → **Turn On Pairing Mode**, and add it to Home Assistant with the code it shows.

## Why not Google Home, Amazon Alexa or SmartThings — yet

- **Amazon Alexa** has no interface that lets another app switch the devices in your Alexa account; it only goes
  the other way (devices exposed *to* Alexa).
- **Google Home** offers its Home APIs to iPhone and Android apps only, behind a Google Cloud project and a Google
  sign-in. Its new Home MCP server needs Google Home Premium Advanced and is US-only for now.
- **Samsung SmartThings** needs an OAuth app registered with Samsung; its personal tokens expire after 24 hours.

Until then, the practical route for any of them is Home Assistant: it has integrations for SmartThings and Google
Nest, and Matter devices from a Google or Amazon ecosystem can be shared with Home Assistant
(Matter "multi-admin": in the Google Home or Alexa app, open the device → **Linked Matter apps & services**).

## Privacy

Credentials stay on your PC, in `%APPDATA%\HouseControl\`, each file encrypted with Windows' Data Protection API
for your Windows account — another account on the PC, or a copy of the file on another PC, cannot read it:
`homeassistant.json` (address and token) and `hue.json` (bridge address, key and certificate fingerprint).
**Disconnect** deletes them. Nothing goes through a server of ours — there is none.

When something does not react, **About House Control → Reveal Device Log** in the tray icon's menu (or **Settings →
Home → Device log**) shows each command and the answer.
