# Simple Unity Digital Twin — Quadruped Robot

A **real-time digital twin** for a low-cost quadruped robot (ESP32-C3).
The Unity client mirrors the robot's posture over Wi-Fi at 20 Hz and can also drive it back.

> 中文说明见 [README.zh-CN.md](README.zh-CN.md)

---

## What it does

| Direction | Path |
| --- | --- |
| **Robot → Unity** | 4 servo angles + body state streamed over WebSocket at 20 Hz; Unity rebuilds the pose, body tilt and face |
| **Unity → Robot** | 17 actions, 4 drive directions, 21 emotions, 6 lamp modes pushed from an in-app panel |
| **No hardware** | An offline simulator drives the same pipeline, so the project runs without a device |

The firmware side adds a **telemetry channel** to an existing open-source robot firmware
(which previously pushed only event notifications, never continuous posture),
plus a fix for a startup crash on ESP32-C3's single I2S controller.

---

## Architecture

```
┌──────────────────────────────┐
│  ESP32-C3 firmware (C/C++)   │
│  · servo gait engine         │
│  · OLED eye renderer         │
│  · HTTP + WebSocket server   │
└───────────┬──────────────────┘
            │  ws://<ip>/ws
            │  telemetry 20 Hz (≈260 B/frame)
            │  commands on demand
┌───────────┴──────────────────┐
│  Unity client (C#)           │
│  · socket thread + queue     │
│  · pose solve (plane fit)    │
│  · face texture renderer     │
│  · control panel / recorder  │
└──────────────────────────────┘
```

## Repository layout

```
unity/            16 C# scripts (drop into Assets/)
firmware/
  digital-twin.patch   the firmware diff (+382 / −103)
  files/               4 modified firmware files (full copies)
  UPSTREAM_NOTES.md    what changed and why
docs/
  code-walkthrough.md  file-by-file walkthrough (Chinese)
  firmware-changes.md  firmware change rationale (Chinese)
  operation-guide.md   step-by-step operating manual (Chinese)
```

## Technical highlights

**1. Telemetry protocol (firmware side, +382 lines)**

- New message types: `telemetry` (subscribe, 1–30 Hz), `snapshot` (single frame)
- **Per-connection subscription flag** — a browser that never subscribes receives nothing
- Main loop split into **50 ms telemetry tick + 200 ms housekeeping tick**, so the pose
  rate stays stable without speeding up Wi-Fi/state-machine work
- JSON built by hand into a pre-reserved buffer (**one allocation per frame**) instead of
  cJSON, because the board's largest free block is only ~38 KB

**2. Pose reconstruction (Unity side)**

The telemetry carries only four joint angles — no body attitude. The client:

```
joint angles → foot positions (forward kinematics)
             → plane fit over four feet (Newell's method)
             → body rotation = yaw delta × rest world rotation × tilt
```

**3. Face rendering**

The robot draws 21 expressions from an 8-parameter eye model. The Unity client re-implements
the **exact same parameters and math** (signed-distance rounded rectangles, lid carving),
so both faces match instead of merely looking similar.

**4. Model-agnostic binding**

A rig adapter measures the model (up/forward axis, leg order, leg length) and rebinds the
client automatically, so any exported FBX that honours the rig contract works.

## Protocol

| Type | Direction | Purpose |
| --- | --- | --- |
| `hello` | device → client | connection greeting, advertises `"telemetry": true` |
| `telemetry` | client → device | subscribe: `{"type":"telemetry","hz":20}` |
| `telemetry` | device → client | pose frame (below) |
| `snapshot` | client → device | one pose frame, no subscription |
| `ping` / `pong` | both | round-trip measurement (echoes `t`) |
| `action` | client → device | 17 action names (`stand`, `sit`, `wave`, …) |
| `drive` | client → device | `none` / `forward` / `backward` / `left` / `right` |
| `emotion` | client → device | 21 emotion names or `auto` |
| `lamp` | client → device | `on` / `off` / `breathe` / `rainbow` / `flow` / `flash` |
| `speed` · `trim` · `chat` | client → device | tuning and voice-chat toggle |

Pose frame:

```json
{"type":"telemetry","t":123456,"state":"idle","voice":false,"emotion":"neutral",
 "action":"stand","busy":false,"drive":"none","speed":100,
 "legs":[90,90,90,90],"trims":[0,0,0,0],
 "lamp":{"on":true,"mode":2,"brightness":60,"speed":100,"r":255,"g":107,"b":53}}
```

`legs` are **logical** angles (`90` = leg straight down, `>90` = forward). Mirroring for the
right-hand servos is applied inside the firmware and never travels over the wire — which is
why the client must *not* mirror again.

## Quick start

### Unity side

1. Create a 3D project (tested on Unity 2022.3).
2. Copy `unity/*.cs` into `Assets/`.
3. Build the scene:
   - empty GameObject `Dog`, add `DogClient` + `DogBodySolver` + `DogSimulator` + `DogControlPanel`
   - drop your quadruped FBX as a child, then run **Tools ▸ Rig adapt to my model**
4. Set `DogClient.deviceHost` to the robot IP, or tick `offlineSimulation` to run without hardware.
5. Press Play.

> Without a model, `Tools ▸ Build twin rig` generates a placeholder rig so the pipeline can be
> verified immediately.

### Firmware side

```bash
# against the upstream project (see acknowledgements)
git apply firmware/digital-twin.patch
idf.py set-target esp32c3
idf.py build flash monitor
```

Or copy the four files from `firmware/files/` over their counterparts.

## Known limitations

| Item | Note |
| --- | --- |
| **Audio input disabled** | ESP32-C3 has a single I2S controller; the board uses an I2S microphone plus an I2S amplifier at different rates, which cannot run simultaneously. The firmware now degrades gracefully instead of aborting at boot — speaker works, microphone does not. |
| Wake word | Not enabled in this configuration (`WakeNet: no`). |
| Position is estimated | Telemetry carries no position; the client integrates drive commands, so drift accumulates over time. |
| Smoothing is frame-rate dependent | Pose interpolation uses a fixed per-frame coefficient, so low frame rates lag more. Measured and documented, not fixed. |
| Single device | One robot per client instance. |

## Acknowledgements

This project builds on the open-source quadruped firmware
**[xiaozhi-esp32-caiyudog](https://github.com/Zohlz/xiaozhi-esp32-caiyudog)** (MIT).
The upstream project provides the hardware platform, gait engine, expression engine,
web console and voice framework.

This repository contributes:

- the telemetry protocol extension, dual-tick scheduling and subscription mechanism (firmware)
- the ESP32-C3 single-I2S graceful-degradation fix
- the complete digital-twin client (16 scripts) — upstream contains no Unity code
- a measurement harness for latency, frame gaps and pose-sync error

## License

MIT — see [LICENSE](LICENSE). Upstream copyright notices are retained.
