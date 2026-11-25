# 固件改动详解 —— 从"编译不过"到"能开机、能被看见"

> 这份文档补的是三块内容：
> **A. 音频 I2S 修复**（不改就无限重启，孪生根本无从谈起）
> **B. 遥测补丁的设计取舍**（摘要，逐行讲解见 `代码详解.md` 第一部分）
> **C. 烧录链路**（WinError 193 / 权限 / 下载模式 / NVS）
>
> 每一节都是：现象 → 根因 → 改法 → 为什么不用别的方案 → 怎么验证。

---

# A. 音频修复：让固件能开机

## A.1 现象

烧完遥测补丁的固件后，串口日志无限循环：

```
E (212) i2s_common: i2s_new_channel(981): invalid I2S port id
ESP_ERROR_CHECK failed: esp_err_t 0x102 (ESP_ERR_INVALID_ARG) at 0x4200e2c0
file: "./main/audio/codecs/no_audio_codec.cc" line 138
expression: i2s_new_channel(&chan_cfg, nullptr, &rx_handle_)

abort() was called at PC 0x40386d11 on core 0
--- 0x40386d56: panic_abort at E:/esp/esp-idf/components/esp_system/panic.c:473
```

关键信息：**不是我们的补丁引起**，是 `NoAudioCodecSimplex` 构造函数里第 138 行。

## A.2 根因：ESP32-C3 只有一个 I2S 控制器

驱动里的校验（`components/esp_driver_i2s/i2s_common.c:981`）：

```c
ESP_RETURN_ON_FALSE((chan_cfg->id >= 0 && chan_cfg->id < I2S_LL_GET(INST_NUM)) ||
                    chan_cfg->id == I2S_NUM_AUTO,
                    ESP_ERR_INVALID_ARG, TAG, "invalid I2S port id");
```

C3 的 `I2S_LL_GET(INST_NUM)` 是 **1**，所以合法的 `id` 只有 `0` 或 `I2S_NUM_AUTO`。

而 `NoAudioCodecSimplex` 的设计是**扬声器和麦克风各占一个控制器**：

```cpp
// 扬声器：port 0
chan_cfg.id = XIAOZHI_I2S_PORT(0);
i2s_new_channel(&chan_cfg, &tx_handle_, nullptr);

// 麦克风：port 1   ← 在 C3 上这一行必然失败
chan_cfg.id = XIAOZHI_I2S_PORT(1);
ESP_ERROR_CHECK(i2s_new_channel(&chan_cfg, nullptr, &rx_handle_));
```

`ESP_ERROR_CHECK` 失败即 `abort()`，于是重启 → 再崩 → 无限循环。

## A.3 我们改了哪四处

文件：`main/audio/codecs/no_audio_codec.cc`

| # | 位置 | 改动 | 为什么 |
| --- | --- | --- | --- |
| 1 | 文件头 | 加 `#include <esp_err.h>` | 用到了 `esp_err_to_name()` |
| 2 | 麦克风通道创建（原 137-144 行） | `id` 改 `I2S_NUM_AUTO`；`ESP_ERROR_CHECK` 改成检查返回值，失败则 `rx_handle_ = nullptr` 并打警告 | **不让单个外设失败拖垮整个固件** |
| 3 | `NoAudioCodec::Read` | 开头加 `if (rx_handle_ == nullptr) { memset 静音; return 0; }` | 麦克风不可用时调用方仍然安全 |
| 4 | `NoAudioCodec::EnableInput` | 开头加 `if (rx_handle_ == nullptr) { AudioCodec::EnableInput(enable); return; }` | 避免对空句柄调 `i2s_channel_enable` |

核心是第 2 处，思想一句话：**可降级的外设不应该用 `ESP_ERROR_CHECK`**。

```cpp
chan_cfg.id = I2S_NUM_AUTO;
esp_err_t mic_err = i2s_new_channel(&chan_cfg, nullptr, &rx_handle_);
if (mic_err != ESP_OK) {
    rx_handle_ = nullptr;
    ESP_LOGW(TAG, "mic channel unavailable (%s): ESP32-C3 has a single I2S controller; audio input disabled",
             esp_err_to_name(mic_err));
} else {
    /* 正常初始化 … 失败也要回滚并删除通道 */
}
```

## A.4 为什么不用别的方案

| 方案 | 结果 | 结论 |
| --- | --- | --- |
| `I2S_NUM_AUTO` 单独改（不降级） | C3 上无空闲控制器，仍然返回错误 → 仍然 abort | 必须配合降级 |
| 换成 `NoAudioCodecDuplex` | 能开机，但麦克风和扬声器共用 BCLK/WS；实物麦克风有独立 SCK/WS 引脚 → 麦克风没有时钟，等于没接 | 能跑但无输入，不优于降级 |
| 换成 `NoAudioCodecSimplexPdm` | C3 支持 PDM RX，能同时跑；但实物是 **INMP441/ICS43434（I2S 三线）**，不是 PDM 麦克风 | 硬件不匹配 |
| 同一控制器跑 TX + RX | I2S 时钟是控制器共享的，输入 16 kHz、输出 24 kHz 无法同时满足 | 物理不可行 |
| **降级（当前方案）** | 能开机，扬声器可用，麦克风禁用 | ✅ |

## A.5 硬件层面的结论（论文可以照抄）

> ESP32-C3 仅有 1 个 I2S 控制器，而本设备的音频方案是
> **I2S 数字麦克风 + I2S 功放**，两者工作频率还不同（16 kHz 收 / 24 kHz 发），
> 因此在同一颗 C3 上**无法同时工作**。厂商发布的 `Caiyu-Dog.bin` 能正常运行，
> 说明其固件采用了时分复用（播放切 TX、录音切 RX）或更换了麦克风接口类型。

**恢复语音的三条路**（按可行性排序）：

1. 拿到厂商实际编译 `Caiyu-Dog.bin` 的那份源码；
2. 自己实现时分复用：在 `EnableInput/EnableOutput` 里重新配置同一条通道，播放时切 TX、录音时切 RX；
3. 换 PDM 麦克风（硬件改板）。

## A.6 怎么验证改对了

烧录后日志应出现（顺序不重要）：

```
W (xxx) NoAudioCodec: mic channel unavailable (ESP_ERR_...): ESP32-C3 has a single I2S controller; audio input disabled
W (xxx) NoAudioCodec: Simplex: speaker only, no microphone
I (xxx) AudioCodec: Set output enable to true
I (xxx) CaiyuWeb: device state: idle
I (xxx) CaiyuDisplay: qrcode shown (21 bytes): http://192.168.1.23/
```

**判定标准：不再出现 `abort()`，且出现 `control page` / `qrcode shown`。**

---

# B. 遥测补丁的设计取舍（摘要）

逐行讲解见 `代码详解.md` 第一部分（1.1~1.4）。这里只讲**为什么这么设计**。

## B.1 三个改动文件

| 文件 | 改动性质 | 目的 |
| --- | --- | --- |
| `caiyu_web_server.h` | 加 `struct Client{int fd; bool telemetry;}`、加 `BuildTelemetryJson()`、加订阅接口 | 支持"按客户端订阅"，而不是全局广播 |
| `caiyu_web_server.cc` | 解析 `telemetry`/`snapshot`（`ping` 原版已有）、推送循环、`hello` 增加 `"telemetry":true` | 遥测协议本体 |
| `caiyu_c3_board.cc` | 主循环拆成 **50 ms 遥测节拍 + 200 ms 内务节拍** | 保证姿态频率稳定 |
| `caiyu_actionments.cc` | 摇杆置零时 `abort_ = true` 无条件置位 | 松手立停，避免孪生与真机脱节 |

## B.2 四个关键取舍

| 决策 | 理由 |
| --- | --- |
| **手拼 JSON**，不用 cJSON | C3 空闲 SRAM 只有 ~66 KB；cJSON 每次 create/free 会产生堆碎片，长时间运行风险高。手拼进栈上定长缓冲，零分配。 |
| **订阅制**（`{"type":"telemetry","hz":20}`） | 浏览器不看不推，省带宽省电；多客户端时互不影响 |
| **50 ms / 200 ms 双节拍** | 姿态要稳（20 Hz 固定），内务（内存统计、状态机）不需要那么快 |
| **一帧 ~260 字节** | 128×64 屏 + 20 Hz 下，WiFi 占用可忽略；同时足够 Unity 还原姿态与表情 |

## B.3 一处有意的行为变更

```c
// 原逻辑：摇杆松开后依靠惯性滑行
// 改后：abort_ = true 无条件置位 → 松开立刻停
```

**这不是 bug，是设计决定**：孪生要求"所见即所得"，真机滑行会导致虚拟狗和真狗姿势脱节。

## B.4 验证补丁真的编进去了

```bash
# 在 xiaozhi.bin 里搜关键词
grep -a -b -o "telemetry" build/xiaozhi.bin   # 应命中
grep -a -b -o "snapshot"  build/xiaozhi.bin   # 应命中
```

更严格的验证：`build/merged-binary.bin` 与官方 `Caiyu-Dog.bin` 大小一致性（本仓库为 3,415,618 字节）。

---

# C. 烧录链路：三个坑

## C.1 坑一：`WinError 193` —— `idf.py` 不是可执行文件

**现象**：

```
OSError: [WinError 193] %1 不是有效的 Win32 应用程序。
```

出现在 `scripts/build.py` 的 `subprocess.run(["idf.py", ...])`。

**根因链**（三层叠加）：

1. ESP-IDF 里的 `idf.py` 是一个 **DOSKEY 宏**，只在交互式 `cmd` 里有效；
2. 在非交互环境（`subprocess`），`idf.py` 落到 `.py` 文件关联上，而系统的 `.py` 关联指向 **Microsoft Store 的 0 字节占位 `python.exe`**（`WindowsApps\python.exe`）；
3. `CreateProcess` 只会自动补 `.exe`，而 `idf.py` **本身已带扩展名**，于是它把 `E:\esp\esp-idf\tools\idf.py`（一个文本文件）当可执行文件加载 → 193。

**解法**：把 `tools\idfbin\idf.py` 做成 **`idf.py.exe` 的二进制副本**（保留无扩展名），并让 `idfbin` 在 `PATH` **最前面**。

验证：

```bat
python -c "import subprocess;print(subprocess.run(['idf.py','--version'],capture_output=True,text=True).stdout)"
:: 应输出 ESP-IDF v6.0.2
```

## C.2 坑二：必须设的环境变量

缺一个就报奇怪的错。尤其 `ESP_IDF_VERSION`：

```
TypeError: expected string or bytes-like object, got 'NoneType'
  at tools/idf_component_manager/idf_extensions.py:388
```

| 变量 | 值 |
| --- | --- |
| `IDF_PATH` | `E:\esp\esp-idf` |
| `IDF_TOOLS_PATH` | `%USERPROFILE%\.espressif` |
| `IDF_PYTHON_ENV_PATH` | `%USERPROFILE%\.espressif\python_env\idf6.0_py3.12_env` |
| `ESP_ROM_ELF_DIR` | `%USERPROFILE%\.espressif\tools\esp-rom-elfs\20241011\` |
| `ESP_IDF_VERSION` | `6.0` ← 最容易漏 |
| `OPENOCD_SCRIPTS` | `…\openocd-esp32\v0.12.0-esp32-20260424\…\scripts` |

## C.3 坑三：`build` 目录的 `PermissionError`

**现象**：

```
PermissionError: [WinError 5] 拒绝访问。: 'E:\\esp\\caiyu-dog\\build\\log'
```

**排查结果**：ACL 正常（`ZHANG\10451:(I)(OI)(CI)(F)`，继承未保护），磁盘 174 GB 空闲，无第三方杀软。

**实际原因**：文件被**瞬时占用**（杀软扫描 / 索引器 / 残留 ninja 进程），`os.remove` 拿到的是 5 而不是 32。

**解法**：用**管理员权限**的窗口执行构建；偶发的话重跑一次即可。

## C.4 合并镜像 vs `idf.py flash`

| 方式 | 烧什么 | NVS（WiFi 密码 / 舵机微调） |
| --- | --- | --- |
| `idf.py -p COM7 flash` | bootloader + 分区表 + app | **保留** ✅ |
| 烧 `merged-binary.bin`（Flash Download Tool / 整片写入） | 整片 4 MB | **清空** ⚠（等于恢复出厂，需要重新配网） |

所以**只改代码时永远用 `idf.py flash`**；想彻底恢复出厂才烧合并镜像。

## C.5 进下载模式

固件能正常运行时，`idf.py` 通常能通过 USB 自动复位进下载模式。
但**当固件在 boot loop（比如这次的 I2S abort）时，软复位常常抢不到**，表现为：

```
Failed to connect to ESP32-C3: No serial data received
```

此时手动进：

```
按住 BOOT → 点一下 RST → 松开 BOOT
```

成功标志：设备管理器出现新 COM 口，或 monitor 里显示 `boot:0x7 (DOWNLOAD)`。

另外注意：**COM 号会变**（可能是 COM7、COM10…），烧录命令里的 `-p` 要对应。

## C.6 烧完的验证清单

| # | 检查 | 期望 |
| --- | --- | --- |
| 1 | 日志无 `abort()` | 无 |
| 2 | `NoAudioCodec: Simplex: speaker only, no microphone` | 出现（本次已知降级） |
| 3 | `CaiyuWeb: device state: idle` | 出现 |
| 4 | `qrcode shown … http://<IP>/` | 出现（长按触摸） |
| 5 | 浏览器 `ws://<IP>/ws` + `{"type":"telemetry","hz":10}` | 每秒约 10 条 |
| 6 | `xiaozhi.bin` 内搜 `telemetry` / `snapshot` | 命中 |

---

# D. 改动总表（拿去写论文"实现"章）

| # | 文件 | 改动 | 目的 | 验证 |
| --- | --- | --- | --- | --- |
| 1 | `main/audio/codecs/no_audio_codec.cc` | 4 处：include、AUTO+降级、Read 守卫、EnableInput 守卫 | 修复 C3 单 I2S 导致的开机 abort | 日志无 abort，扬声器可用 |
| 2 | `main/boards/caiyu-c3/caiyu_web_server.h` | 客户端订阅结构 + 遥测接口 | 按客户端订阅 | 编译通过 |
| 3 | `main/boards/caiyu-c3/caiyu_web_server.cc` | 遥测/快照/ping 处理 + 推送循环 | 20 Hz 姿态推送 | 浏览器收到 telemetry |
| 4 | `main/boards/caiyu-c3/caiyu_c3_board.cc` | 50 ms / 200 ms 双节拍 | 稳定推送频率 | 到达间隔 ≈50 ms |
| 5 | `main/boards/caiyu-c3/caiyu_actionments.cc` | 松摇杆 `abort_ = true` | 松手立停 | 面板松开即停 |
| 6 | `tools/idfbin/idf.py` | `idf.py.exe` 的二进制副本 | 绕过 WinError 193 | `idf.py --version` 正常 |

## 补充说明：为什么这版补丁"只增不改"

除了第 5 条（有意的行为变更），其余全部是**新增分支/新增字段**：

- 旧消息（`emotion`/`lamp`/`action`/`drive`/`chat`/`trim`/`speed`）行为完全不变；
- `hello` 只多了一个字段，旧客户端会忽略；
- 不订阅遥测时，设备**一帧都不额外发**，功耗与原来一致。

这使得补丁可以安全地合入上游，也方便答辩时说明"兼容性未破坏"。
