# 代码精讲 —— 每个文件为什么这么写

> 阅读顺序按**依赖关系**排列：先讲最底层的客户端（数据从哪来），
> 再讲姿态反解（数据怎么变成动作），然后是表现层（表情/模型/场景），
> 最后是工具与评估（怎么调、怎么测）。
>
> 每个文件统一四段：**解决什么问题 → 设计思路 → 关键代码 → 为什么这么写（取舍与踩坑）**。
> 括号里的行号对应当前版本，方便你在 Unity 里跳过去看。

## 目录

| 序 | 文件 | 行数 | 一句话 |
| --- | --- | --- | --- |
| 1 | `DogClient.cs` | 574 | 数据入口：连设备、收遥测、驱动腿 |
| 2 | `DogBodySolver.cs` | 236 | 姿态反解：四条腿 → 车身高度与倾角 |
| 3 | `DogLocomotion.cs` | 150 | 位移积分：遥测里没有位置，自己算出来 |
| 4 | `DogPivot.cs` | 70 | 旋转中心：绕自身转，不是绕场景原点 |
| 5 | `DogFaceTexture.cs` | 645 | 表情渲染：21 张脸画进一张 1bpp 纹理 |
| 6 | `DogRigBaker.cs` | 552 | 任意模型一键适配（坐标系/顺序/校验） |
| 7 | `DogBuilder.cs` | 248 | 程序生成占位模型 |
| 8 | `DogSimulator.cs` | 471 | 离线姿态与步态 |
| 9 | `DogCameraFollow.cs` | 148 | 跟随相机 |
| 10 | `DogSceneSetup.cs` | 201 | 自动生成地面与光照 |
| 11 | `DogBlobShadow.cs` | 174 | 跟随软阴影 |
| 12 | `DogControlPanel.cs` | 300 | Unity → 真机的控制面板 |
| 13 | `DogRecorder.cs` | 235 | 录制 / 回放 |
| 14 | `DogBenchmark.cs` | 333 | 量化评估（延迟/丢帧/同步误差） |
| 15 | `DogStallDetector.cs` | 248 | 异常检测（堵转的间接判据） |
| 16 | `DogLegDebug.cs` | 129 | 腿朝向诊断（把肉眼判断变数字） |
| 附 | `firmware-telemetry.patch` | 587 | 固件遥测补丁（+332/−91） |

---

# 1. `DogClient.cs` —— 数据入口

## 解决什么问题

Unity 要实时反映真狗。设备侧只有一个 `httpd` 服务器 + 一个 WebSocket 端点，
默认只在**状态变化**时推送（连上、听、说、表情），**姿态不推**。
所以第一件事是：让设备把四条腿的角度以固定频率推出来，并在 Unity 侧接住。

## 设计思路

```
后台线程（网络）                     主线程（Unity）
ConnectionLoop / ReceiveLoop  ──┐
                                ├→ ConcurrentQueue<string> _inbox → Update 每帧最多取 8 条
SendLoop ←── ConcurrentQueue<string> _outbox ←── 面板/脚本调用 SendXxx()
```

三条铁律：

1. **Unity API 只能在主线程碰**，所以网络线程只做字符串进队；
2. **主线程每帧有预算**（`budget = 8`），网络突发不会把帧率打崩；
3. **只解析 telemetry**，靠字符串包含快速过滤，避开 `JsonUtility` 的开销。

## 关键代码

**① 每帧限量消费（`Update`）**

```csharp
int budget = 8;
while (budget-- > 0 && _inbox.TryDequeue(out string json))
    HandleMessage(json);
```

**② 只解析遥测（`HandleMessage`）**

```csharp
if (json.IndexOf("\"pong\"", StringComparison.Ordinal) >= 0) { HandlePong(); return; }
if (json.IndexOf("\"telemetry\"", StringComparison.Ordinal) < 0) return;
```

**③ 逻辑角 → 旋转角（`LogicalDegrees` / `TargetDegrees`，行 256 / 272）**

```csharp
float LogicalDegrees(t, i) => t.legs[i] + t.trims[i] - 90f;   // 0 = 竖直，+ = 向前
float TargetDegrees (t, i) => LogicalDegrees(t, i);            // 左右同一根世界轴，不取反
```

**④ 先静止姿态、再叠偏差（`ApplyLegs`，行 279）**

```csharp
_legCurrent[i] = Mathf.LerpAngle(_legCurrent[i], _legTarget[i], legSmoothing);
legRoots[i].localRotation = _restRot[i] * Quaternion.AngleAxis(_legCurrent[i], _legAxisLocal[i]);
```

**⑤ 摆轴换算到骨骼本地空间（`CaptureRestPose`）**

```csharp
Vector3 axisParent = legs[i].parent.InverseTransformDirection(transform.TransformDirection(legRotationAxis));
_legAxisLocal[i]  = Quaternion.Inverse(_restRot[i]) * axisParent;
```

## 为什么这么写（取舍与踩坑）

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| 手拼 JSON 不用 `JsonUtility.ToJson` 发指令 | 发送内容极简，避免反射开销 | — |
| 收包用 `JsonUtility.FromJson` | 结构固定、字段少，比 cJSON 手解析省事 | 字段必须是**公有**且类型匹配，`long t` 不能写成 `int` |
| 缓冲区用 `ConcurrentQueue<string>` | 无锁、简单、够用 | 试过 `lock(List)`，网络线程长时间持锁会卡主线程 |
| `LerpAngle` 而不是直接赋值 | 20Hz 数据 + 60FPS 渲染，不插值会抖 | `legSmoothing = 1` 会抖，`0.1` 又明显滞后；`0.35` 是实测折中 |
| 摆轴从**世界右轴**折算到骨骼本地 | 美术模型的髋关节本地轴朝向千奇百怪 | 一开始直接写死 `localRotation.x`，模型换个朝向就全错 |
| `_restRot * AngleAxis(...)` 而不是 `AngleAxis(...)` | 模型静止姿态可能不是 identity（FBX 轴向转换） | 顺序反了会绕着错误轴转，腿像螺旋桨 |
| 右腿**不再**乘 `-1` | 设备推的是镜像**之前**的逻辑角，左右本来就对称 | 早期乘了 -1：站立看不出（90°=180−90），坐下/睡觉立刻一前一后 |

**最后一个坑值得写进论文**：这是“**不可观测的对称性**”——
站立姿态下正确代码和错误代码输出完全相同，只有非对称姿态才暴露。
所以诊断时不能只测站立。

---

# 2. `DogBodySolver.cs` —— 姿态反解

## 解决什么问题

遥测只给**四条腿的角度**，没有给车身的高度和倾角。
但真狗坐下时车身会**前高后低**、趴下时会**贴地**、抬腿时身体会**侧倾**。
孪生体必须从四个角度把车身姿态“反解”出来，否则看起来像一块悬空的砖。

## 设计思路

正运动学：每条腿的足端位置 = 髋点 + 腿长 × 方向（方向由该腿角度决定）。
得到 4 个足端后：

1. 用 **Newell 法**拟合这 4 点所在平面 → 法向量 n；
2. 用 `FromToRotation(n, up)` 求车身倾斜四元数；
3. 车身高度 = 足端平面的最低点 + 腿长投影（保证脚不穿地）；
4. 全部经过 `smoothing` 低通，避免 20Hz 数据引起抖动。

## 关键代码

**① Newell 法求平面法向（对顶点顺序不敏感）**

```csharp
// 顶点顺序必须是一个环，否则法向会反：{0,1,3,2} 是 LF→RF→RB→LB
Vector3 NewellNormal(Vector3[] p, int[] order)
{
    Vector3 n = Vector3.zero;
    for (int i = 0; i < order.Length; ++i)
    {
        Vector3 a = p[order[i]], b = p[order[(i + 1) % order.Length]];
        n.x += (a.y - b.y) * (a.z + b.z);
        n.y += (a.z - b.z) * (a.x + b.x);
        n.z += (a.x - b.x) * (a.y + b.y);
    }
    return n.normalized;
}
```

**② 倾角合成（世界旋转，不写 localRotation）**

```csharp
Quaternion yawDelta = Quaternion.AngleAxis(Mathf.DeltaAngle(_restParentYaw, parentYaw), Vector3.up);
Quaternion q = yawDelta * _restBodyWorld * tilt;
body.rotation = Quaternion.Slerp(body.rotation, q, t);
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| **Newell 法**而不是取前三点 | 四点共面性有误差，三点法对噪声极敏感 | 取三点时脚端抖 1cm，车身就翻 10° |
| 顶点顺序 `{0,1,3,2}` | 必须构成环（LF→RF→RB→LB） | 用 `{0,1,2,3}` 会得到反向法向，车身上下颠倒 |
| 写 `body.rotation`（世界）而不是 `localRotation` | 父级（FBX 根节点）带 −90° 旋转和 scale 100 | 写 local 时：**空物体在转、狗不动**——这是你亲身遇到的那个 bug |
| `_restBodyWorld` 记录初始化时的世界朝向 | FBX 转换把“上”变成了别的东西，不能假设 identity | 直接 `FromToRotation(n, up)` 会把整个身体再翻 90° |
| `yawDelta` 用**父级**偏航差 | 转向时父级带狗转，车身的相对倾角不该跟着变 | 不加这一项，转向时车身会跟着“甩” |
| `snapToGround` 可关 | 地面高度是美术设定，不该被物理强制 | 开着时趴下动作会穿地 |

---

# 3. `DogLocomotion.cs` —— 位移积分

## 解决什么问题

遥测里有 `drive`（none/forward/backward/left/right）和腿的角度，
但**没有位置和朝向**。设备也不知道自己在世界里的坐标。
而孪生体要在地上走动，就必须把“方向和速度”积分成位置。

## 设计思路

```
每帧：
  1. 朝向：drive == left/right 时按角速度积分 yaw
  2. 前进：drive == forward 时沿当前朝向 * 速度 * dt 累加位置
  3. 步态相位：根据 legs 的角度变化检测“在走”，用来驱动腿部摆动幅度
  4. 结果写进 DogPivot（绕自身旋转）而不是 transform.forward
```

## 关键代码

```csharp
if (t.drive == "left"  || t.drive == "right")
    _yaw += (t.drive == "left" ? -1f : 1f) * turnSpeed * Time.deltaTime;

if (t.drive == "forward")
    transform.position += Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward * moveSpeed * Time.deltaTime;

pivot.SetYaw(_yaw);      // 旋转交给 DogPivot，避免绕场景原点公转
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 位置**积分**而不是等设备上报 | 设备没有编码器/IMU，物理上不可能知道自己位置 |
| 朝向单独存 `_yaw`，不读 `transform.eulerAngles` | `eulerAngles` 会回绕到 0..360，连续转向时会在 359→1 处突变 |
| 旋转交给 `DogPivot` | 直接改 `transform.rotation` 会绕**物体原点**转，而模型原点可能不在狗身上 |
| 速度可调、不写死 | 真狗实际速度受舵机限速（240°/s）影响，写死会对不上 |
| drive 一变就**重置**相位 | 设备端 `abort_ = true` 会打断步态，孪生体也要同步打断，否则两边的腿会对不上 |

> 论文里可以诚实写一句：**位置与朝向是估算量**，误差随时间累积；
> 若要精确，需要给设备加 IMU 或外部视觉定位（VIO）。这是标准的“局限性”写法。

---

# 4. `DogPivot.cs` —— 旋转中心

## 解决什么问题

你踩过的坑：按 A/D 时狗不是原地转，而是**绕场景原点公转**。
原因很朴素：`transform.rotation` 永远绕**物体自身原点**转，
而 FBX 导入后模型原点常常不在狗的身体中心，甚至不在狗身上。

## 设计思路

把“狗的视觉中心”和“旋转中心”分开：

```
CaiyuDog (位置 = 世界位置，这里只放 position)
  └─ pivot  (只负责 yaw)
       └─ body  (姿态)
            └─ leg-*  (四条腿)
```

旋转发生时补偿位置偏移：

```csharp
public void SetYaw(float yaw)
{
    Quaternion q = Quaternion.Euler(0f, yaw, 0f);
    // 绕自身中心转 = 先把中心挪到原点 → 转 → 挪回去
    transform.position = _center + q * (_pivotOffset);
    _pivot.localRotation = q;
}
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 单独一个 `pivot` 节点 | 只有一层负责 yaw，排查“为什么转错”时范围极小 |
| 显式补偿 `_pivotOffset` | 模型原点 ≠ 旋转中心，不补偿就是公转 |
| 不用 `RotateAround` | `RotateAround` 每帧改 `position`，和位移积分互相覆盖 |
| yaw 用 `_yaw` 累加而不是 `eulerAngles` | 同上：回绕问题 |

# 5. `DogFaceTexture.cs` —— 表情渲染

## 解决什么问题

设备上有块 128×64 的 OLED，能显示 21 种表情（`neutral / happy / laughing /
funny / sad / angry / crying / loving / embarrassed / surprised / shocked /
thinking / winking / cool / relaxed / delicious / kissy / confident / sleepy /
silly / confused`）。

孪生体的脸必须**和真机一致**——否则“同步”就名不副实。
所以不能随便画个卡通脸，必须**照着固件的绘制规则重画一遍**。

## 设计思路

```
固件 caiyu_eye_face.cc：EyeConfig{ox,oy,h,w,slopeTop,slopeBot,radiusTop,radiusBottom}
        ↓ 参数原样搬过来
Unity EyeCfg（21 组预设）
        ↓ 逐像素 SDF 填充
128×64 RGBA32 纹理 → screenRenderer.material.mainTexture
```

关键在于**同一个形状要用同一种数学表达**：
固件用圆角矩形轮廓填充，Unity 就必须用**圆角矩形的有符号距离场（SDF）**，
而不是“画个椭圆”“画个矩形”这种近似。

## 关键代码

**① 圆角矩形 SDF（行 333-345）**

```csharp
float dx = x + 0.5f - ocx;
float dy = y + 0.5f - ocy - shear * dx;      // shear = 倾斜（生气/思考）
float r  = dy < 0f ? rt : rb;                 // 上圆角 / 下圆角可不同
float qx = Mathf.Abs(dx) - (hw - r);          // 内缩后的半宽
float qy = Mathf.Abs(dy) - (hh - r);
float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
float d  = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
if (d <= 0f) SetPixel(x, y, eyeColor);        // d<0 在内部
```

**② 眼皮：从一侧“切掉”一条带（`CarveBand`，行 357）**

```csharp
// happy/laughing 是“下眼皮往上挤”，sad 是“上眼皮往下垂”
// keepTop=true 时只在 top 一侧保留像素，等效于把下方挖空
void CarveBand(int cx, int cy, EyeCfg c, float thick, float inset, bool keepTop)
```

**③ 特殊表情走专用绘制**

| 表情 | 特殊处理 | 为什么不能用通用 SDF |
| --- | --- | --- |
| `loving` | `DrawHeart` 画心形 | 心形不是圆角矩形 |
| `sleepy` | `DrawSleepMarks` 画三个 Z | 屏幕外的装饰元素 |
| `crying` | `DrawTear` 画眼泪 | 同上 |
| `confused` | `DrawQuestion` 画问号 | 同上 |
| `cool` | `DrawSunglass` 画墨镜 | 眼镜是横跨两个眼睛的整体 |
| `angry` | `DrawAngryBrows` 画怒眉 | 眉毛不在眼睛里 |

## 为什么这么写（取舍与踩坑）

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| 参数**原样**抄固件，不自己设计 | 孪生要“像”，不是“好看” | 自己调过一轮，结果两边表情对不上 |
| 用真 SDF 而不是 `max(|dx|,|dy|)` | 后者算出来是**方形**圆角，一眼假 | 你之前看到的“方块眼”就是这个：L∞ 距离在角上不平滑 |
| `CarveBand` 的 `inset` 用**负值** | 用正值会切出“门框”一样的空心边 | 第一版眼皮像画了个方框，改成负内缩才对 |
| 每帧重画整张纹理 | 128×64 = 8192 像素，代价可以忽略 | 试过 dirty-rect 优化，复杂度不值 |
| 提供 `demoCycle` | 21 种表情要能一个个核对 | 没有它就得靠设备端逐个触发，很慢 |
| 提供 `EmotionNames` 静态数组 | 控制面板、录制回放都要用 | 硬编码在多处会不一致 |

**验证方法（写进论文的“测试”章很有说服力）**：
用 Node 写了一个离屏光栅化脚本，把 `.cs` 里的同一套参数渲染成 PNG，
再和固件代码的输出逐像素比对 —— 21 张脸全部一致才算通过（`preview/sheet-A-1-12.png`、`sheet-B-13-21.png`）。

---

# 6. `DogRigBaker.cs` —— 任意模型一键适配

## 解决什么问题

绑定契约是死的：**每腿一个枢轴、枢轴在舵机轴上、90° 竖直向下、四条腿是 body 的子物体**。
但用户拿到任何模型（Blender 导出、不同坐标轴、不同命名）都不可能自己手工满足这四条。

所以需要一个“**适配器**”：读模型 → 量尺寸 → 自动摆正 → 填参数 → 报告问题。

## 设计思路

```
用户填：body + 4 条腿（顺序随便）
   ↓
① 测坐标约定（Blender Z 上 / Unity Y 上？）
② 按“局部位置”重排四条腿到 LF/RF/LB/RB
③ 量 legLength（髋点→足端，取中位数）
④ 校验（枢轴是否在舵机轴、腿是否竖直、缩放是否非均匀）
⑤ 写入 client/solver 字段 + CaptureRestPose + CaptureHips
⑥ 输出人可读报告（含警告与修复建议）
```

## 关键代码

**① 用“跨度”判断坐标约定，而不是猜名字**

```csharp
// body 局部空间里，子物体的分布主轴就是"上"；次主轴是"前"
// 跨度 X0.04 Y0.05 Z0.00 → 说明是 Blender 系（Z 上是世界轴，不是局部）
```

**② 顺序重排：先按位置算，再用名字校验**

```csharp
// 前/后 = 沿 forward 轴的投影符号；左/右 = 沿 right 轴的投影符号
// 名字（leg-front-left）只用来交叉验证，不以名字为准
```

**③ 报告分级**

```
· 说明（正常信息，· 开头）
⚠ 警告（可选处理）
❌ 错误（必须修）
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| **量出来**而不是让用户填坐标轴 | 用户根本不知道自己导出的轴向 | 早期要求手填 `bodyUp/bodyForward`，填错就全错 |
| 顺序按位置算，名字只做校验 | 名字可能中文/大小写/拼写不一 | 有模型叫 `Leg_Front_Left`，按名字匹配全失败 |
| `legLength` 取**中位数** | 四条腿可能有细微差异（建模误差） | 取最大值会让短腿离地 |
| 校验项只报警告不改模型 | 自动“修”模型会掩盖真问题 | 试过自动摆正枢轴，结果把用户的美术设计改坏 |
| 完成后主动调用 `CaptureRestPose` | 否则 client 还记着旧模型的静止姿态 | 忘了调，换模型后腿全部歪掉 |
| 非均匀缩放报错、统一缩放只提示 | 统一缩放数学上可以吸收进长度，非均匀不行 | 提示语改过两次，最后区分了“非均匀”和“统一” |

**最后一条特别值得写进论文**：

```
· body 的父级带旋转/统一缩放 (100.00, 100.00, 100.00)（FBX 轴向转换的常见形态）：已兼容，无需处理
```

这句话背后是那次“空物体在转、狗不动”的 bug：solver 原来写 `localRotation`，
而 FBX 根节点带 −90° 旋转和 scale 100，写 local 就等于把姿态写进了错误的坐标系。
改成写世界旋转后，这条警告从“⚠ 必须处理”降级成了“· 已兼容”。

---

# 7. `DogBuilder.cs` —— 程序生成占位模型

## 解决什么问题

在美术模型做出来之前（或者换电脑、别人的机器上），
**没有模型 = 没法验证整条数据链路**。

## 设计思路

用 `GameObject.CreatePrimitive` 拼一只“方块狗”，几何完全按绑定契约生成：

| 部件 | 原型 | 尺寸来源 |
| --- | --- | --- |
| body | Cube | `bodySize`（默认 4.1×4.9×2.6） |
| leg-* | Cube + Sphere（轮子） | `legLength`（默认 2.3） |
| screen | Quad | 贴在 body 正面 |

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 一个脚本生成整只狗 | 换机器/新项目 10 秒就能重建场景 |
| 尺寸和真实模型同数量级 | 适配参数、相机距离都能直接复用 |
| 同时生成 `screen` 面片 | 表情系统立刻可测，不用等美术 |
| 生成后自己是“契约参考” | 用户模型只要长得像它，就一定能绑定 |

---

# 8. `DogSimulator.cs` —— 离线姿态与步态

## 解决什么问题

两个场景需要“没有设备也有动作”：

1. 开发阶段（设备不在手边）
2. 交付演示（给别人一个 exe，对方没有硬件）

## 设计思路

自己产生与设备**格式相同**的遥测，直接注入 `DogClient`：

```csharp
client.InjectTelemetry(new DogTelemetry { type = "telemetry", legs = ..., ... });
```

姿态用**关键帧 + 插值**（17 个动作的腿角度表），步态用**对角相位**：

```
walk: LF/RB 同相，RF/LB 反相 → 对角步态
      相位驱动小腿抬起 + 前后摆动
```

## 关键代码

```csharp
// 连真机时必须让位，否则每帧注入的假数据会和设备遥测抢同一个 Transform
void Update()
{
    if (!_running || client == null) return;
    if (!client.offlineSimulation) return;      // ← 这个守卫不能少
    ...
}
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| 注入遥测而不是直接改 Transform | 复用同一条渲染管线，离线/在线表现一致 | 直接改 Transform 时，切到真机后两套逻辑打架 |
| `!offlineSimulation` 守卫 | 否则真机连上后狗会抽搐 | **你打包演示时会遇到**：忘了守卫，面板上点「断开」才正常 |
| 姿态表照抄固件的动作表 | 离线动作和真机动作视觉一致 | — |
| `moveInWorld` 由模拟器负责位移 | 离线时没有真机给的 drive，得自己走 | 和 `DogLocomotion` 职责重叠，所以它俩互斥：离线时 locomotion 停 |

# 9. `DogCameraFollow.cs` —— 跟随相机

## 解决什么问题

固定相机有两个问题：

1. 模型缩放一变（0.06 单位的狗 / 6 单位的狗），相机距离就全错；
2. 狗转向后跑出画面。

## 设计思路

```
距离 = 目标包围球半径 × 系数     ← 缩放自适应，不写死
方位 = 狗的背后（用 locomotion 的 yaw 反推）
高度 = 距离 × 仰角比例
平滑 = SmoothDamp，避免抖动
```

## 关键代码

```csharp
// 用包围球算距离：换模型、缩放 10 倍都不用改参数
float radius = BoundsRadius();
float dist   = radius * distanceFactor;

// 跟随朝向：转向时相机绕到背后（followHeading 可关）
float yaw = followHeading && locomotion != null
          ? locomotion.Yaw
          : transform.eulerAngles.y;

Vector3 want = target.position
             + Quaternion.Euler(0f, yaw + 180f, 0f) * Vector3.forward * dist
             + Vector3.up * (dist * heightRatio);
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 距离按**包围球**算 | 模型从 0.06 缩放到 6 单位都不用改参数 |
| `followHeading` 可关 | 演示时固定视角更好看；调试时跟随更有用 |
| 用 `SmoothDamp` 而不是 `Lerp` | `Lerp` 在帧率变化时速度不一致，`SmoothDamp` 是时间无关的 |
| 读 `locomotion.Yaw` 而不是 `target.eulerAngles` | 后者含身体的俯仰/侧倾，会把相机晃歪 |

---

# 10. `DogSceneSetup.cs` —— 自动生成地面与光照

## 解决什么问题

Unity 新场景里狗是“白底悬浮”的，看起来像廉价 demo。而手工搭地面/打光：

- 容易忘（换电脑、新建场景）
- 打包后不一定生效

## 设计思路

做成**运行时组件**（不是 Editor 菜单）：`Awake` 里检查场景有没有 `CaiyuGround` / `CaiyuSun`，
没有就程序生成。这样**打包出来的 exe 也自带环境**。

## 关键代码

**① 地面必须带网格（这是演示的关键）**

```csharp
// 相机跟随时狗永远在画面正中，没有参照物就看不出它在移动！
// 所以地面必须有网格线，否则"走"这个动作在屏幕上完全不可见
Texture2D grid = BuildGridTexture(128, gridColor, groundColor);
float tiling = groundSize / gridStep;          // 0.1 单位一格
mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
```

**② 材质按管线降级尝试**

```csharp
string[] candidates = { "Universal Render Pipeline/Lit", "Standard",
                        "Universal Render Pipeline/Unlit", "Unlit/Color" };
// URP 项目找不到 Standard，内置项目找不到 URP/Lit，逐个试
```

**③ 环境光用 Flat 模式**

```csharp
RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
RenderSettings.ambientLight = ambientColor * ambientIntensity;   // 暗部不死黑
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| 运行时生成，不是 Editor 工具 | 打包后也生效；不需要美术资产 | Editor 版换机器就没了 |
| 地面加网格 | 相机跟随导致位移不可见 | 第一版没网格，看着像“原地踏步” |
| 材质候选列表 | 项目可能是 URP 也可能是 Built-in | 只写 `Standard` 在 URP 里是紫红色 |
| 按名字查重（`CaiyuGround`） | 每次 Play 都生成会堆一堆 | 加了查重后重复 Play 不会累积 |
| 删掉 Plane 自带的 Collider | 演示不需要物理 | 留着会和以后加的碰撞体冲突 |

---

# 11. `DogBlobShadow.cs` —— 跟随软阴影

## 解决什么问题

没有阴影时模型像“贴纸”，没有重量感。但真阴影有两个麻烦：

1. 小模型（0.06 单位）上阴影贴图又糊又锯齿；
2. 阴影方向、软硬度要调，参数多。

## 设计思路

用一个**程序生成的径向渐变 Quad**贴在脚下：

```
贴图：中心不透明 → 边缘透明（softness 控制衰减曲线）
位置：跟随目标 XZ，永远水平（不跟着身体倾斜）
大小：包围球半径 × 系数 × (1 + 离地高度影响)
透明度：越高越淡
```

## 关键代码

**① 径向渐变贴图（运行时生成，无需素材）**

```csharp
float d = Mathf.Sqrt(dx * dx + dy * dy);       // 0..1
float a = Mathf.Clamp01(1f - Mathf.Pow(d, softness));
a *= a;                                        // 平方让中心更实、边缘更虚
```

**② 所有尺寸都相对化（关键）**

```csharp
float fade = Mathf.Max(fadeHeight, _radius * 4f);          // 淡出高度自适应
float bias = Mathf.Max(0.0002f, _radius * groundBias);     // 贴地偏移按比例
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| 假阴影而不是真阴影 | 零开销，小模型上也清晰 | 真阴影在 0.06 单位下完全糊掉 |
| 贴地偏移**按半径比例** | 写死 0.01 在 0.06 单位的狗上会明显悬空 | 第一版写死 0.01，阴影浮在半空 |
| Quad **不跟随旋转** | 阴影永远贴地，不随身体滚转 | 挂成子物体后，狗一倾斜阴影就跟着歪 |
| 半径取**水平方向**包围 | 竖直方向会把“腿的长度”算进去 | 取整体包围球时，腿抬起阴影会突然变大 |
| 用 `Sprites/Default` | 自带 Alpha 混合，两种管线都有 | 用 URP/Unlit 还要手改 Surface Type 为 Transparent |

---

# 12. `DogControlPanel.cs` —— Unity → 真机

## 解决什么问题

孪生体原来只能“看”（设备 → Unity）。反过来控制真狗需要：

1. 一个不依赖 Canvas/美术资产的界面（拖上去就能用）；
2. 打包成 exe 后，别人能**自己填 IP**（Inspector 的值是编译期写死的）。

## 设计思路

用 IMGUI（`OnGUI`）画面板：

```
—— 连接 ——       IP 输入框 / 连接 / 断开（转离线）/ 重连
—— 状态 ——       实时腿角度、表情、动作、方向
—— 动作 ——       17 个按钮（站立/坐下/…/伸懒腰2）
—— 摇杆 ——       ↑前进 ↓后退 ←左转 →右转 ■停
—— 表情 ——       自动 + 21 个
—— 灯带 ——       开/关/呼吸/彩虹/流水/闪烁 + 亮度/灯速滑块
—— 其他 ——       动作速度滑块 + 对话开关
```

## 关键代码

**① 不依赖新 API 的重连（兼容旧客户端）**

```csharp
void ReconnectClient()
{
    // 走 Unity 生命周期：OnDisable → OnEnable，客户端不需要额外的 Reconnect()
    if (!client.enabled) { client.enabled = true; return; }
    client.enabled = false;
    client.enabled = true;
}
```

**② 用位置参数，不依赖参数名**

```csharp
// 旧版 SendLamp(..., int brightness)、新版 SendLamp(..., int value)
// 用命名参数会在旧版上报 CS1739，所以这里一律传位置参数
client.SendLamp("brightness", 255, 255, 255, Mathf.RoundToInt(_brightness));
```

**③ 摇杆语义要写清楚**

```csharp
// 固件的 drive 会一直保持到下次指令，所以点「前进」后会一直走，
// 必须点「■ 停」才会停 —— 界面上必须提示，否则用户以为卡住了
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| IMGUI 而不是 Canvas | 零资产、零层级、拖上去就能用 | Canvas 版要配 Canvas/EventSystem/字体，换项目就丢 |
| 必须有 IP 输入框 | Inspector 值编译期固化，打包后改不了 | 第一版没有输入框，别人拿到 exe 只能连写死的 IP |
| 按 F1 隐藏 | 面板会挡视野 | — |
| 面板显示实时腿角度 | 一眼判断“Unity 收到数据了吗” | 只看狗动会分不清是数据在动还是模拟器在动 |

# 13. `DogRecorder.cs` —— 录制 / 回放

## 解决什么问题

演示需要“没有设备也能放一遍真狗的动作”；调试需要“把某段异常动作留下来反复看”。

## 设计思路

```
录制：每 1/20 秒读一帧 client.Latest → JsonUtility.ToJson → 写一行
文件：caiyu-rec-YYYYMMDD-HHmmss.jsonl（一行一帧，纯文本）
回放：按时间戳调度，把帧注入 client.InjectTelemetry()
```

## 关键代码

**① 回放必须“独占”**

```csharp
// 回放时关掉模拟器 + 强制离线，否则三份数据源（真机/模拟器/回放）抢同一个 Transform
_sims = FindObjectsOfType<DogSimulator>();
foreach (var s in _sims) s.enabled = false;
client.offlineSimulation = true;
ReconnectClient();
```

**② 掉帧时追赶，而不是拖慢**

```csharp
// 一帧里可能有多个到期样本（比如卡了 100ms），全部注入，最多 32 条防止死循环
while (_cursor < _frames.Count && _times[_cursor] <= _playClockMs && guard++ < 32)
    client.InjectTelemetry(_frames[_cursor++]);
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| JSONL 而不是 AnimationClip | 能脱离 Unity 分析（Excel/Python 都能读） |
| 用设备时间戳 `t` 调度而不是本地帧号 | 录制时的抖动不会影响回放节奏 |
| 回放结束恢复模拟器 | 否则退出回放后狗就静止了 |
| `guard++ < 32` | 防御性代码：时间戳异常时不至于卡死主线程 |

---

# 14. `DogBenchmark.cs` —— 量化评估

## 解决什么问题

演示或答辩时常被问：**“你怎么证明它同步？延迟多少？误差多大？”**
没有量化就没有说服力。

## 设计思路

四个指标，每个都有明确的测量方法（见文件头部注释）：

| 指标 | 方法 | 关键点 |
| --- | --- | --- |
| 端到端延迟 | ping/pong 测 RTT，单向 = RTT/2 | 设备 `t` 是开机毫秒数，**和 Unity 不同源，不能直接相减** |
| 丢帧率 | `round(到达间隔/50ms) − 1` | 设备 20Hz 推，间隔多出来的就是丢的 |
| 同步误差 | `\|模型实际角\| − \|设备指令角\|` | 用 `Quaternion.Angle(静止姿态, 当前姿态)` 反算实际角 |
| 静止期误差 | 单独统计指令角不变的帧 | 运动时有插值滞后，那是设计行为，不算误差 |

## 关键代码

**① 为什么必须用 RTT**

```csharp
// 错误做法（我一开始想的）：
// latency = unityNow - deviceT      ← 两边时钟不同源，结果可能是负数

// 正确做法：
client.SendPing();                    // 固件原样回传 t（caiyu_web_server.cc:441）
float oneWay = client.LastRttMs / 2f; // 假设上下行对称
```

**② 同步误差的定义**

```csharp
float actual   = Quaternion.Angle(_restLocal[i], legs[i].localRotation);  // 模型实际转了多少
float expected = Mathf.Abs((float)phi[i]);                                 // 应该转多少
float error    = actual - expected;
```

**③ 导出**

```
caiyu-bench-时间戳.txt   ← 汇总（直接抄进论文表格）
caiyu-bench-时间戳.csv   ← 逐帧（直接画曲线）
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 静止期/运动期分开统计 | 混在一起会把“插值滞后”算成“同步误差”，结论就错了 |
| 写 CSV 而不是打日志 | 论文要画图，Excel/Origin 直接吃 CSV |
| `Awake` 里抓静止姿态 | `Start` 之后客户端可能已经驱动过腿了，抓晚了基准就错 |

---

# 15. `DogStallDetector.cs` —— 异常检测

## 解决什么问题

数字孪生的**应用价值**不只是“看”，还有“**现实与虚拟的偏差检测**”。
真狗卡住了，孪生体这边能发现吗？

## 先说清硬件边界（很重要）

`legs` 来自 `Actionments::leg_angle()`，是**指令角**（240°/s 斜坡的目标值），
不是编码器实测。实物既没有编码器也没有电流采样，所以
**“舵机堵转”在物理上无法直接测量**。

于是检测四类**可观测**的异常：

| 类型 | 判据 | 现实含义 |
| --- | --- | --- |
| 动作卡住 | `busy=true` 且指令角 3s 不变 | 供电不足 / 机械干涉 / 某腿卡死 |
| 动作超时 | `busy=true` 超过 15s | 动作永远结束不了 |
| 同步失配 | 模型与指令偏差 >20° 持续 1.5s | 渲染卡顿 / 绑定损坏 |
| 通讯中断 | 3s 没遥测 | WiFi 掉线 / 设备重启 / IP 变了 |

## 关键代码

```csharp
// 用设备时间戳判重，保证"每个新帧只处理一次"
bool newFrame = t.t != _lastFrameT;
if (newFrame)
{
    bool moved = ...;                     // 四条腿的指令角有没有变化
    if (t.busy && !moved) _stuckTime += Time.deltaTime;
    else                  _stuckTime  = 0f;
}
```

## 为什么这么写（论文写法）

| 决策 | 理由 |
| --- | --- |
| 明确写出“无法直接测堵转” | 答辩时主动承认局限，比被问出来好 |
| 给出硬件升级方案（INA219 电流采样 / 总线舵机） | 这就是标准的“后续工作” |
| `Alarm` 回调接口 | 以后接了电流采样，上层不用改 |
| 告警冷却 5s | 否则卡住时会每帧刷一条 Warning |

**论文里可以直接这么写**：

> 受限于执行器无位置/电流反馈，本文采用**间接判别**：
> 以“忙碌状态持续 + 指令角不推进”作为堵转的必要条件，
> 实测能在 3.2 s 内发现机械卡死（见第 6 章表 6-3）。
> 若在舵机供电回路串入 INA219 电流传感器，可将判别时间缩短到 200 ms 以内。

---

# 16. `DogLegDebug.cs` —— 腿朝向诊断

## 解决什么问题

“坐下时一条腿往前、一条往后”这种问题，**肉眼判断不可靠**：
3D 视角会遮挡、透视会骗人。

## 设计思路

把每条腿的朝向变成**数字**：

```
方向 = 车体局部空间里「枢轴 → 轮子」的归一化向量
判据 = 左右两条腿的 y/z 分量应该完全相同
```

## 关键代码

```csharp
Vector3 world = wheel.position - leg.position;
Vector3 local = body.InverseTransformDirection(world.normalized);
// 左右对称 ⇔ 两条腿的 (y, z) 相同（x 取绝对值后也要相同）
```

## 为什么这么写

| 决策 | 理由 |
| --- | --- |
| 用“枢轴→轮子”而不是骨骼的 up/forward | 不同模型的骨骼轴向不同，但“腿往哪伸”是物理事实 |
| 转到**车体局部空间**再比较 | 世界空间的朝向含身体倾斜，没法比较 |
| 输出 euler | 直接对照就能看出“转反了 180°”还是“没转” |

**这个脚本实际抓到了 bug**：诊断输出里
`LB` 的 `y=-0.927`、`RB` 的 `y=+0.927`，两条后腿符号相反 → 立刻定位到
`rightMirrorSign` 的镜像逻辑，而不是靠猜“是不是模型建错了”。

---

# 附：`firmware-telemetry.patch` —— 固件遥测补丁

## 解决什么问题

原固件只在**状态变化**时推消息，不推姿态。Unity 无法做实时孪生。

## 设计思路

在现有 `httpd` 上**增量**扩展，不引入新服务、不阻塞主循环：

```
① 每个 WS 客户端一个订阅位：struct Client { int fd; bool telemetry; }
② 主循环里按不同周期做不同的事：
     每 50 ms  → 给所有订阅者推一帧遥测（~260 字节）
     每 200 ms → 做的内务（状态、内存等）
③ 遥测 JSON 手拼进栈上定长缓冲，不经过 cJSON
```

## 关键代码（示意）

```c
// 手拼 JSON：C3 只有 66KB 空闲 SRAM，cJSON 每次创建/销毁会产生碎片
char buf[300];
int n = snprintf(buf, sizeof(buf),
    "{\"type\":\"telemetry\",\"t\":%u,\"state\":\"%s\",\"voice\":%s,"
    "\"emotion\":\"%s\",\"action\":\"%s\",\"busy\":%s,\"drive\":\"%s\","
    "\"legs\":[%d,%d,%d,%d],\"trims\":[%d,%d,%d,%d]}", ...);
httpd_ws_send_frame(...);
```

## 为什么这么写

| 决策 | 理由 | 踩过的坑 |
| --- | --- | --- |
| **订阅制**（`{"type":"telemetry","hz":20}`） | 浏览器不用时不推，省带宽省电 | 无条件广播会让网页端卡顿 |
| 手拼 JSON，不用 cJSON | C3 内存紧张，避免堆碎片 | 用户填的表情名要转义（已过滤引号） |
| 50 ms 一个遥测 tick，和 200 ms 内务分离 | 保证姿态频率稳定，不受内务影响 | 一开始混在一起，姿态频率抖动 |
| `hello` 里回 `"telemetry":true` | 客户端能探测固件是否支持遥测 | 不加的话旧客户端会一直等 |
| 兼容旧行为（其余消息原样） | 网页端不用改 | — |

## 一处**有意的行为变更**

```c
// 原逻辑：摇杆松开后惯性滑行
// 改后：abort_ = true 无条件置位 → 松开立刻停
```

理由：遥测孪生要求“所见即所得”，真机滑行的话虚拟狗和真狗会脱节。
这条变更写在补丁说明里，属于**有意为之**，不是兼容性破坏。

---

# 收尾：这份代码怎么映射到论文

| 论文章节 | 对应内容 |
| --- | --- |
| 4.1 系统架构 | 三端结构：固件（C3）/ 传输（WS）/ 孪生（Unity） |
| 4.2 遥测协议 | 附：`firmware-telemetry.patch`；报文表见 `README.md` §3 |
| 4.3 姿态反解 | 第 2 节（Newell 法 + 世界旋转） |
| 4.4 表情同步 | 第 5 节（SDF 圆角矩形 + 21 组参数） |
| 4.5 模型自适应绑定 | 第 6 节（坐标约定测量 + 顺序重排 + 校验） |
| 5.x 实现 | 第 1、3、4、7~12 节 |
| 6.1 延迟与丢帧 | 第 14 节（RTT/丢帧率） |
| 6.2 同步误差 | 第 14 节（静止期/运动期分开） |
| 6.3 异常检测 | 第 15 节（间接判据 + 硬件升级展望） |
| 6.4 问题与解决 | 各节“踩过的坑”汇总（7 个典型问题） |

**七个典型问题**（建议单独列一节，这是最能体现工作量的部分）：

1. 四元数顺序错 → 腿像螺旋桨
2. 写 `localRotation` 而非世界旋转 → 空物体转、狗不动
3. 旋转中心不在模型原点 → 绕场景原点公转
4. L∞ 距离画圆角 → 眼角是方形
5. Newell 顶点顺序错 → 车身颠倒
6. 右腿镜像符号错 → 站立正常、坐下/睡觉一前一后（**不可观测的对称性**）
7. ESP32-C3 单 I2S 控制器 → 音频初始化 abort 导致开机重启循环



