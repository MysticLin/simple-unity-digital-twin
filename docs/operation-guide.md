# 操作手册 · 从编译到数字孪生联调

> 照着从上往下做，每一步都有「成功标志」和「失败怎么办」。
> 设计原理看 `架构设计总览.md`，模型细节看 `Unity建模与绑定指南.md`。

---

## 阶段 0 · 现状确认

| 项 | 状态 |
| --- | --- |
| 硬件焊接 | ✅ 完成（供电接触问题已修） |
| 原版固件 | ✅ 已烧 v2.4.2，动作/表情/触摸/灯光全通过 |
| 舵机装配 | ✅ 已按 90°=竖直向下重装 |
| 网络 | 设备在**家用 2.4GHz Wi-Fi**，IP `192.168.1.23`（PC 也要在这个热点里） |
| 待完成 | 装 Python → 装 IDF → 打补丁 → 编译 → 烧录 → 验证遥测 → Unity |

预计总耗时：**2~4 小时**（其中大部分是下载等待）。

---

## 阶段 1 · 装 Python 3.12.10

### 下载

```
https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe
```

> ⚠ 必须是 **3.12.10**。实测 3.12.11～3.12.16 在 python.org 上没有 amd64 安装包（全是 404）。
> 本地已有一份：`E:\Dsh\DSH\xuexi\tools\python-3.12.10-amd64.exe`（25.7 MB），可直接用。

### 安装

1. 双击安装包
2. **第一页底部勾上 `Add python.exe to PATH`** ← 不勾后面全崩
3. 点 **Install Now**
4. 等 1~2 分钟

### 成功标志

新开一个 PowerShell：

```powershell
python --version      # Python 3.12.10
pip --version         # pip 24.x
```

### 失败怎么办

| 现象 | 处理 |
| --- | --- |
| `python` 打开的是应用商店 | PATH 没生效 → 重装并勾选 Add to PATH，或手动加 `%LOCALAPPDATA%\Programs\Python\Python312` |
| `pip` 不存在 | 重装时确认 Include pip 勾上 |

---

## 阶段 2 · 装 ESP-IDF v6.0.2

离线安装器页面目前只到 5.5.x，所以走源码安装（v6.0.2 标签已确认存在）。

### 命令（cmd 或 PowerShell 都可以）

```bat
set IDF_GITHUB_ASSETS=dl.espressif.cn/github_assets

git clone -b v6.0.2 --depth 1 --recurse-submodules --shallow-submodules https://github.com/espressif/esp-idf.git E:\esp\esp-idf

cd E:\esp\esp-idf
install.bat esp32c3
```

- 第 1 行**必须**先执行：把工具链下载指向国内镜像，否则从 GitHub 拉会非常慢
- `--depth 1 --shallow-submodules`：只拉最新一层，省一半体积
- `install.bat esp32c3`：只装 C3 的工具链，再省 2~3 GB
- 磁盘需求约 **3 GB**

### 成功标志

```bat
E:\esp\esp-idf\export.bat
idf.py --version
```

应显示 `ESP-IDF v6.0.2`。

> `export.bat` 会新开一个 cmd 窗口，**之后所有 `idf.py` 命令都在这个窗口里跑**。

### 失败怎么办

| 现象 | 处理 |
| --- | --- |
| `git clone` 卡住/极慢 | 先确认 `IDF_GITHUB_ASSETS` 那行已执行；或用 Gitee 镜像 `https://gitee.com/EspressifSystems/esp-idf.git` |
| `schannel: AcquireCredentialsHandle failed` | 换 `git config --global http.sslBackend openssl` 后重试 |
| `install.bat` 报找不到 python | 关掉窗口重开（让 PATH 生效），再跑一次 |
| 路径带空格/中文 | IDF 对路径敏感，**必须**用 `E:\esp\esp-idf` 这种纯英文无空格路径 |

---

## 阶段 3 · 拿源码 + 打遥测补丁

```powershell
git clone https://github.com/Zohlz/xiaozhi-esp32-caiyudog.git caiyu-dog
cd caiyu-dog

git apply --check ".\firmware-telemetry.patch"
git apply          ".\firmware-telemetry.patch"
```

### 成功标志

`git apply --check` **没有任何输出**（无声即成功）。

### 失败怎么办

补丁冲突就直接覆盖 4 个文件：

```powershell
copy ".\firmware\caiyu_*.*" caiyu-dog\main\boards\caiyu-c3\
```

### 验证补丁进去了

```powershell
findstr /C:"telemetry" caiyu-dog\main\boards\caiyu-c3\caiyu_web_server.cc
```

能搜到内容就说明打上了。

---

## 阶段 4 · 开日志输出（强烈建议）

固件控制台默认走 **UART0 物理引脚**，你的板子没有 UART 转接芯片 → 意味着**看不到任何启动日志**，只能靠屏幕和网页猜。

### 做法

在 `caiyu-dog\sdkconfig.defaults` **末尾加一行**：

```
CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y
```

### ⚠ 关键：必须删掉旧的 sdkconfig

`sdkconfig.defaults` **只在生成新的 sdkconfig 时生效**。如果目录里已有 `sdkconfig`，改了也不生效：

```powershell
cd caiyu-dog
del sdkconfig
del sdkconfig.old
```

### 效果

- 固件运行时 **COM7 会一直存在**（不再消失）
- `idf.py monitor` 能看到 `I (123) boot: ESP-IDF v6.0.2 ...`、`control page: http://...`

---

## 阶段 5 · 编译

在 `E:\esp\esp-idf\export.bat` 打开的窗口里：

```bat
cd caiyu-dog
python scripts/build.py caiyu-c3
```

- 首次会下载 `esp-sr` 等托管组件，**10~20 分钟**，需要外网
- 会自动套用 `sdkconfig.defaults.esp32c3`（4MB flash + `partitions/v2/4m.csv`）

### 成功标志

```
Project build complete. To flash, run:
 idf.py flash
```

### 失败怎么办

| 报错 | 处理 |
| --- | --- |
| 组件下载失败/超时 | 挂代理，或设 `IDF_COMPONENT_REGISTRY_URL` 指向镜像 |
| `unknown board` | 板名必须是 `caiyu-c3`（`python scripts/build.py --list-boards` 可查） |
| 内存不足/编译中断 | 关掉 Unity 等大程序再编 |

---

## 阶段 6 · 烧录

### 先做一件事：断开舵机 5V 电源

烧完启动瞬间 4 个舵机齐动，电流尖峰可能把电压拉下去。信号线可以留着，**5V 供电线拔掉**。

### 烧录

```bat
idf.py -p COM7 flash monitor
```

> COM 号先确认：设备管理器 → 端口 → 名字带 `USB 串行设备` 的那个。

### 成功标志

日志里出现：

```
I (xxx) boot: ESP-IDF v6.0.2 ...
I (xxx) CaiyuC3Board: ...
I (xxx) caiyu_servo: servo attached: gpio=1 ch=0 mirror=0 init=90
I (xxx) CaiyuC3Board: control page: http://192.168.1.23/
```

### 连不上怎么办

```
Failed to connect to ESP32-C3: No serial data received
```

→ 按住 **BOOT → 点 RST → 松开 BOOT** → 重跑命令。不用手动进下载模式，`idf.py` 平时会自己处理。

### 退出监视器

按 `Ctrl+]`。

### 烧完

接回舵机 5V 电源。

---

## 阶段 7 · 验证遥测（关键一步）

浏览器打开 `http://192.168.1.23/`，按 F12 → Console，粘：

```js
const ws = new WebSocket('ws://192.168.1.23/ws');
ws.onopen = () => ws.send(JSON.stringify({ type: 'telemetry', hz: 10 }));
ws.onmessage = e => console.log(e.data);
```

### 成功标志

每秒约 **10 条**：

```json
{"type":"telemetry","t":1234,"state":"idle","voice":"idle","emotion":"sleepy",
 "action":"sleep","busy":false,"drive":"none","speed":0,
 "legs":[90,90,180,0],"trims":[0,0,0,0],
 "lamp":{"on":true,"mode":2,"brightness":60,"speed":100,"r":255,"g":107,"b":53}}
```

### 同时验证订阅隔离

手机打开控制页 → **收不到** telemetry（只有主动订阅的客户端才收）。这是设计如此。

### 失败怎么办

| 现象 | 处理 |
| --- | --- |
| 一条都没有 | 补丁没打上 → 回阶段 3 检查 `findstr telemetry` |
| 有数据但 `legs` 不变 | 正常：静止时腿角度不变 |
| 网页打不开 | 设备掉线 → 看 `idf.py monitor` 日志；确认 PC 和设备在同一网段 |

---

## 阶段 8 · Unity 装配

### 8.1 导入

1. Unity 新建 3D 项目（2022.3 已装好）
2. 把 `.\*.cs`（9 个）拷进 `Assets/`
3. `Dog.fbx` 拖进场景（**先确认 FBX 里没有 Camera / Light**，有就删）

### 8.2 层级

```
CaiyuDog  (空物体，Transform 归零，位置 0,0,0)
├─ DogClient
├─ DogBodySolver
├─ DogLocomotion
├─ DogFaceTexture
└─ Dog  (FBX 实例)
```

### 8.3 逐项填字段

| 组件 | 字段 | 填什么 |
| --- | --- | --- |
| `DogClient` | `deviceHost` | `192.168.1.23` |
| | `offlineSimulation` | **先勾上**（离线验证阶段） |
| `DogBodySolver` | `body` | FBX 里的 `body` |
| | `hips` | 4 条腿（LF/RF/LB/RB 顺序） |
| | `groundY` | `0` |
| `DogLocomotion` | `client` / `solver` | 拖同物体的两个组件 |
| `DogFaceTexture` | `client` | 拖 `DogClient` |
| | `screenRenderer` | 见 8.5 |

### 8.4 适配模型（关键）

菜单 **Tools ▸ 菜鱼狗 ▸ 适配我自己的模型** → 填 `body` + 4 条腿 → 点「① 自动适配」。

报告应长这样：

```
· 坐标约定：Blender 系（Z 上 / -Y 前）→ 已自动换算
  body局部 up=(0,0,1) forward=(0,-1,0)  跨度 X4.10 Y4.90 Z0.00
· 已适配：legLength = 2.289
```

**尺寸问题**：如果 `legLength` 只有 `0.023` 这种极小值，先把根节点缩放到 **10**，**再点一次适配**（`legLength` 是适配时量的，缩放后必须重测）。

### 8.5 表情屏

1. 选中 FBX 里的 `screen` 物件
2. 材质换成 **Unlit**（URP: `Universal Render Pipeline/Unlit`；Built-in: `Unlit/Texture`）
3. 把它的 `Renderer` 拖到 `DogFaceTexture.screenRenderer`
4. 贴图**不用预放**，运行时自动生成 128×64

### 8.6 相机

Main Camera 挂 `DogCameraFollow`：

| 字段 | 值 |
| --- | --- |
| `target` | `CaiyuDog` |
| `solver` | 同一个 `DogBodySolver` |
| `locomotion` | 同一个 `DogLocomotion` |
| `followHeading` | ☑ 打开（转弯时绕到背后） |

### 8.7 离线验证（不接硬件）

再挂一个 `DogSimulator`，进 Play：

| 操作 | 期望 |
| --- | --- |
| `1` / `2` / `3` | 站 / 坐（鼻子抬 12°）/ 睡 |
| `W` / `S` | 前进 / 后退（**对角步态**） |
| `A` / `D` | **原地自转**（不是平移、不是绕圈） |
| 勾 `demoCycle` | 21 种表情轮播，Console 打印名字 |

### 8.8 切真机

1. 取消勾 `DogClient.offlineSimulation`
2. 进 Play，手机网页点动作 → **真狗和虚拟狗同时动**

---

## 阶段 9 · 校准（一次只改一个）

| 测试 | 症状 | 改哪个 |
| --- | --- | --- |
| `walk` | 像在后退 | `DogBodySolver.swingSign = -1` |
| `wave` | 举错腿 | `DogRigBaker.swapLeftRight = true` 重新适配 |
| `turn_left` | 转反 | 同上 |
| 静止 | 高度明显偏 | 卡尺实测后覆盖 `legLength` |
| 四腿 | 左右对折 | `DogClient.rightMirrorSign = +1` |
| 表情 | 左右镜像 | `DogFaceTexture.flipHorizontal` |
| 相机 | 太远/太近 | `DogCameraFollow.distanceFactor` |
| 转向 | 太快/太慢 | `DogLocomotion.turnRate` |

改完**一项就测一次**，不要一次改多个。

---

## 阶段 10 · 验收

- [ ] `idf.py monitor` 能看到日志
- [ ] 浏览器收到 20 Hz telemetry
- [ ] Unity 里狗会站/坐/睡/走/转
- [ ] 21 种表情都能显示
- [ ] 真机动作 → 虚拟狗同步（延迟 < 200 ms）
- [ ] 语音对话正常
- [ ] 灯带同步

---

## 附录 A · 命令速查

```bat
:: 打开 IDF 环境
E:\esp\esp-idf\export.bat

:: 编译
cd caiyu-dog
python scripts/build.py caiyu-c3

:: 烧录 + 看日志
idf.py -p COM7 flash monitor

:: 只烧 app（不碰 bootloader/分区表）
idf.py -p COM7 app-flash

:: 清理重编
idf.py fullclean
```

```powershell
:: 找设备 IP（PC 与设备同网段时）
node "tools/find_device.js" 192.168.1.

:: 探测设备 HTTP + WS
node ".\tools\probe_device.cjs" 192.168.1.23
```

## 附录 B · 报错对照表

| 现象 | 根因 | 处理 |
| --- | --- | --- |
| `Failed to connect to ESP32-C3` | 没进下载模式 | BOOT+RST 后再跑 |
| 烧完不启动 | 芯片还在下载模式 | 拔插 USB（别碰 BOOT） |
| 烧录中途失败 | 舵机电流导致 brownout | 烧前拔舵机 5V |
| 看不到日志 | 控制台走 UART0 | 阶段 4 加 `CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y` 并删 sdkconfig |
| `git apply` 冲突 | 仓库有新提交 | 改用直接覆盖 |
| `python` 是应用商店 | PATH 未生效 | 重装勾 Add to PATH |
| `schannel` 凭证错误 | git SSL 后端问题 | `git config --global http.sslBackend openssl` |
| Unity 里狗躺倒 | `bodyUp` 探测错 | 手动设 `bodyUp=(0,0,1)`、`bodyForward=(0,-1,0)` |
| Unity 里空物体转狗不转 | solver 覆盖父级偏航 | 用最新版脚本（`yawDelta × restBodyWorld × tilt`） |
| `A/D` 绕圈公转 | 旋转中心不在模型上 | 右键 `DogLocomotion` ▸ 重新计算旋转中心 |

## 附录 C · 我放在工具目录的脚本

| 脚本 | 用途 |
| --- | --- |
| `tools/find_device.cjs` | 扫局域网找设备 IP（按 MAC `3c-0d-0d` 定位） |
| `tools/probe_device.cjs` | 探测设备 HTTP + WebSocket `hello` |
| `tools/flash_c3.ps1` | 零依赖串口烧录器（备选，GUI 工具挂掉时用） |
| `tools/build_caiyu_dog.py` | Blender 生成模型 |
| `tools/inspect-fbx.cjs` | 校验 FBX 是否满足绑定契约 |
| `tools/verify_model_params.cjs` | 校验模型参数自洽 |

## 附录 D · 关于网络

现在设备在**家用 2.4GHz Wi-Fi**里。热点的问题：IP 每次变、手机熄屏可能断、Unity 每次要改 `deviceHost`。

**建议在联调成功之后再换**到家里 2.4G 路由器（步骤：重烧整合镜像清 NVS → 热点配网选 2.4G → xiaozhi.me 重新绑定 → 用 `find_device.cjs` 重新找 IP）。

不要一次改太多，否则出问题分不清是哪一步引起的。

---

## 附录 E · 2026-10-30 实测更新（重要，官方补丁之外多改了一处）

### E.1 编译产物里的 `xiaozhi.bin` 开机 abort → 必须改音频代码

**现象**（烧完补丁固件后无限重启）：

```
E (212) i2s_common: i2s_new_channel(981): invalid I2S port id
ESP_ERROR_CHECK failed: esp_err_t 0x102 (ESP_ERR_INVALID_ARG)
file: ./main/audio/codecs/no_audio_codec.cc line 138
expression: i2s_new_channel(&chan_cfg, nullptr, &rx_handle_)
```

**原因**：`ESP32-C3` 只有 **1 个 I2S 控制器**（驱动校验 `chan_cfg->id < I2S_LL_GET(INST_NUM)`），
而 `NoAudioCodecSimplex` 需要第二个控制器跑麦克风（`XIAOZHI_I2S_PORT(1)`）。

**改法**：`main/audio/codecs/no_audio_codec.cc`

| 位置 | 改动 |
| --- | --- |
| 第 4 行 | 加 `#include <esp_err.h>` |
| 麦克风通道创建 | `chan_cfg.id = I2S_NUM_AUTO;`，失败时打警告并把 `rx_handle_ = nullptr`，**不要 `ESP_ERROR_CHECK`** |
| `NoAudioCodec::Read` | 开头 `if (rx_handle_ == nullptr) { memset 静音; return 0; }` |
| `NoAudioCodec::EnableInput` | 开头 `if (rx_handle_ == nullptr) { AudioCodec::EnableInput(enable); return; }` |

改完固件能启动，**扬声器可用，麦克风被禁用**（硬件限制，见 E.3）。

### E.2 Unity → 真狗的反向控制（`DogControlPanel.cs`）

固件本来就支持这些指令（`caiyu_web_server.cc` 的 `HandleMessage`）：

| type | 字段 | 取值 |
| --- | --- | --- |
| `action` | `action` | 17 个动作名（stand/sit/sleep/prone/walk/walk_back/turn_left/turn_right/wave/stretch/swing/scratch/kneel/front_prone/sway/raise_hips/shake_back_legs） |
| `drive` | `dir` | none / forward / backward / left / right |
| `emotion` | `emotion` | 21 个表情名 或 `auto` |
| `lamp` | `action` + `r/g/b` + **`value`** | on/off/breathe/rainbow/flow/flash/brightness/speed/query |
| `chat` | `action` | `toggle` |
| `speed` | `value` | 50~200 |
| `trim` | `index`+`value` 或 `action`=`save`/`reset` | -20~20 |
| `telemetry` | `hz`(1~30) / `enable` | 订阅姿态 |
| `snapshot` | — | 只取一帧 |

> ⚠ `brightness` 和 `speed` 读的是 **`value`** 字段，不是 `brightness`（`caiyu_web_server.cc:511/513`）。
> `DogClient.SendLamp()` 两个都发，兼容改名。

Unity 侧用法：`DogControlPanel` 挂到 `CaiyuDog`、`client` 拖进去，Play 后左上角出现面板，`F1` 开关。

**摇杆类指令点一次会一直保持**，要停必须点「■ 停」。

### E.3 已知遗留问题

| 问题 | 影响 | 原因 |
| --- | --- | --- |
| `WakeNet: no` | 「你好小智」唤醒词不工作 | 唤醒词模型没进固件/assets |
| 麦克风不可用 | 只能听不能说 | C3 单 I2S 控制器，I2S 麦克风 + I2S 功放无法同时工作 |
| 长按出二维码后偶发白屏 | 仅影响 OLED 显示 | QR 层是铺满全屏的白色底板，canvas 没画出来时只剩白底 |

前两个都不影响数字孪生（遥测和动作全部正常）。
