# 固件补丁（遥测）

这个目录里的 4 个文件就是**改好的完整文件**，直接覆盖到仓库同名路径即可：

```
main/boards/caiyu-c3/caiyu_web_server.h       81 → 112 行
main/boards/caiyu-c3/caiyu_web_server.cc     632 → 825 行
main/boards/caiyu-c3/caiyu_c3_board.cc       597 → 614 行
main/boards/caiyu-c3/caiyu_actionments.cc   1379 → 1379 行（只换 6 行）
```

改动内容：新增 WebSocket 消息 `telemetry`（订阅 20 Hz 姿态快照）与 `snapshot`（单帧），
守护任务拆成 50 ms 遥测节拍 + 200 ms 守护节拍，`SetDrive(none)` 立即打断当前步态。

---

## 用法 A：直接覆盖（最省事）

```sh
cd <你的仓库根目录>
cp /path/to/firmware/caiyu_web_server.h      main/boards/caiyu-c3/
cp /path/to/firmware/caiyu_web_server.cc     main/boards/caiyu-c3/
cp /path/to/firmware/caiyu_c3_board.cc       main/boards/caiyu-c3/
cp /path/to/firmware/caiyu_actionments.cc    main/boards/caiyu-c3/

python scripts/build.py caiyu-c3
idf.py -p COM5 flash monitor
```

> ⚠ 覆盖前建议先 `git checkout -b feature/unity-telemetry` 备份。
> 如果你自己改过这几个文件，用下面的用法 B 更安全。

---

## 用法 B：打补丁（保留你自己的其它改动）

补丁在上一级目录：`../firmware-telemetry.patch`

```sh
cd <你的仓库根目录>
git apply --check ../firmware-telemetry.patch     # 先试，无声就是能打
git apply          ../firmware-telemetry.patch
```

补丁相对**官方 main 分支**生成（commit 基线：Zohlz/xiaozhi-esp32-caiyudog @ main）。
若你的仓库已有本地改动导致冲突，改用用法 A 或手工按 `../README.md` §3.2–3.4 逐段改。

---

## 验证结果（已实测）

| 项 | 结果 |
| --- | --- |
| `git apply --check` | exit 0 |
| `git apply` | exit 0 |
| 应用后行数 | 112 / 825 / 614 / 1379，与文件版本一致 |
| 内容比对 | 逐字节一致（仅行尾 LF/CRLF 差异，由 `core.autocrlf` 决定） |

**未验证**：本机没装 ESP-IDF，编译需要你跑一次 `python scripts/build.py caiyu-c3`。

---

## 编译后怎么确认补丁生效

设备联网后，浏览器 F12 控制台：

```js
const ws = new WebSocket('ws://192.168.1.23/ws');
ws.onopen = () => ws.send(JSON.stringify({ type: 'telemetry', hz: 10 }));
ws.onmessage = (e) => console.log(e.data);
```

每秒约 10 条 `{"type":"telemetry", ...}` 就说明补丁生效；
手机打开控制页应当**收不到** telemetry（按客户端订阅隔离）。
