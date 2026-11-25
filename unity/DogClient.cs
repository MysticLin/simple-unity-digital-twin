// DogClient.cs —— 菜鱼狗 Unity 数字孪生客户端
//
// 用法：
//   1. 把本脚本挂到场景里任意 GameObject 上，填好 deviceHost（串口日志里那行
//      "control page: http://192.168.x.x/" 的 IP）。
//   2. 把 4 条腿的根 Transform 按 左前、右前、左后、右后 的顺序拖进 legRoots。
//   3. 运行时它会连 ws://<ip>/ws，订阅姿态遥测，并在 Update 里做插值与事件。
//
// 平台：Windows / macOS / Android / iOS（IL2CPP）可用；
//      WebGL 不支持 System.Net.WebSockets，需换成 NativeWebSocket 等插件，
//      只需替换本文件里的传输层，Telemetry 结构与回调保持不变。
//
// 对应固件改动见同目录 README.md（新增 {"type":"telemetry"} 订阅与快照推送）。

using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[Serializable]
public class DogLamp
{
    public bool on;
    public int mode;        // 0 关 1 常亮 2 呼吸 3 彩虹 4 流水 5 闪光
    public int brightness;  // 5-100
    public int speed;       // 百分比
    public int r;
    public int g;
    public int b;
}

[Serializable]
public class DogTelemetry
{
    public string type;
    public long t;          // 设备开机毫秒数
    public string state;    // idle/connecting/listening/speaking/...
    public bool voice;      // VAD
    public string emotion;  // neutral/happy/sleepy/...
    public string action;
    public bool busy;
    public string drive;    // none/forward/backward/left/right
    public int speed;       // 动作速度百分比
    public int[] legs;      // 左前、右前、左后、右后（逻辑角 0-180，90 = 中立）
    public int[] trims;     // 每条腿的微调 ±20
    public DogLamp lamp;
}//这些都是与esp的BuildTelemetryJson严格对齐

public class DogClient : MonoBehaviour
{
    [Header("连接")]
    [Tooltip("设备 IP，串口日志里 control page 那行的地址")]//这些都是用来说明的
    public string deviceHost = "192.168.1.23";//传入你的局域网地址，在外部都可以修改
    public int port = 80;//端口号，一般不用动
    [Range(1, 30)] public int telemetryHz = 20;//订阅遥测帧率
    public bool autoReconnect = true;//是否尝试重连

    [Tooltip("勾上则完全不连设备，改由 DogSimulator 注入假数据（没有硬件时用它先看效果）")]
    public bool offlineSimulation;

    [Header("腿（顺序：左前、右前、左后、右后）")]
    public Transform[] legRoots = new Transform[4];//四条腿关节的Transform
    [Tooltip("摆动轴，按**车体空间**理解，通常就是 Vector3.right（横向轴）。" +
             "换成自己美术模型后不用管髋关节的本地轴朝向，脚本会自动换算。")]
    public Vector3 legRotationAxis = Vector3.right;//腿摆动轴，规定x轴正方向，绕这个轴前后摆动
    /// <summary>
    /// 【已废弃】代码不再读取它，只保留字段以免破坏旧场景的序列化数据。
    /// 以前用它给右腿取反，结果坐下/睡觉时右腿方向反了（左前伸、右后摆）。
    /// </summary>
    [HideInInspector] public float rightMirrorSign = 1f;
    //下面这些到事件上面由dsh辅助编写
    [Tooltip("角度插值系数，1 = 不插值")]
    [Range(0.05f, 1f)] public float legSmoothing = 0.35f;//lerp插值，越小越顺滑，延迟越高

    [Header("身体（可选）")]
    public Transform body;
    [Tooltip("0 = 关闭。>0 用前后腿角度差粗略外推身体俯仰，只是\"看起来像\"，需自己调")]
    [Range(0f, 0.05f)] public float bodyTiltGain = 0f;
    [Range(0.02f, 1f)] public float bodySmoothing = 0.2f;//同理

    [Header("灯带（可选）")]
    public Renderer[] stripRenderers;
    public string emissionColorProperty = "_EmissionColor";
    public float emissionIntensity = 2f;

    [Header("调试")]
    [Tooltip("WASD/方向键直接发 drive，方便没有 UI 时验证")]
    public bool enableKeyboardDebug;
    public bool logMessages;//收到遥测json打印到console，调试用

    // ---- 事件 ----
    /// <summary>每收到一帧快照（主线程回调）。</summary>
    public event Action<DogTelemetry> TelemetryReceived;//收到一帧遥测
    /// <summary>action 字段发生变化。</summary>
    public event Action<string> ActionChanged;//动作切换
    /// <summary>emotion 字段发生变化。</summary>
    public event Action<string> EmotionChanged;//表情切换
    /// <summary>连接状态文字变化。</summary>
    public event Action<string> StatusChanged;//连接状态变换
    //用于解耦，别的脚本可以订阅
    public DogTelemetry Latest { get; private set; }//只读，外部脚本拿到最新一帧的遥测数据
    public string Status { get; private set; } = "未连接";//当前连接状态
    public bool Connected => _ws != null && _ws.State == WebSocketState.Open;//判断web是否联通

    // 状态文字是网络线程写的，这里暂存一下，主线程 Update 再落到 Status 并发事件，
    // 避免在后台线程触发 Unity API。
    volatile string _statusPending;//告诉编译器，该变量被多线程调用，不要优化

    ClientWebSocket _ws;//web客户端实例
    CancellationTokenSource _cts;//用来安全断开连接
    readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();//收件箱，接受json字符串
    readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();//主线程把要发给esp的指令发送出去
    SemaphoreSlim _sendGate;//信号量，限制同一时刻只能有一条消息发送
    //concurrentqueue是线程安全队列，普通队列无法跨线程读写
    readonly float[] _legCurrent = new float[4];//腿当前的角度
    readonly float[] _legTarget = new float[4];//从遥测包拿出来的目标角度
    readonly float[] _legLogical = new float[4];//偏差角
    bool _legsInitialized;//标记是否受到过第一包遥测，没收到不驱动
    string _lastAction;//记录上一帧的动作
    string _lastEmotion;//记录上一帧的表情，只有变化才触发事件，避免每帧重复抛事件
    float _bodyPitch, _bodyRoll;

    // 美术模型自带的静止姿态与换算后的本地摆动轴（见 CaptureRestPose）
    readonly Quaternion[] _restRot =//四条腿静止时候的localRotation
    {
        Quaternion.identity, Quaternion.identity, Quaternion.identity, Quaternion.identity,
    };
    readonly Vector3[] _legAxisLocal =//把面板设置的车体摆动轴，换算到髋关节骨骼本地坐标系的轴
    {
        Vector3.right, Vector3.right, Vector3.right, Vector3.right,
    };
    bool _restCaptured;//标记是否采集过静止状态，用于换模型的时候用
    void Start()
    {
        // 腿的引用可能在编辑期才赋值，这里兜底记录一次静止姿态
        if (!_restCaptured) CaptureRestPose();
    }//start函数是物体激活的时候调用一次，之后不再执行
    void OnEnable()//脚本启用时候调用
    {
        if (offlineSimulation)//如果勾选了离线模式直接返回
        {
            SetStatus("离线模拟（未连设备）");
            return;
        }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectionLoop(_cts.Token));//启动异步循环，在后台线程跑
    }

    void OnDisable()//脚本禁用，或者物体销毁的时候调用
    {
        try { _cts?.Cancel(); } catch { }//取消发送消息
        try { _ws?.Abort(); } catch { }//关闭web
        _cts?.Dispose();
        _cts = null;
        _ws = null;//清空引用
        if (!offlineSimulation) SetStatus("未连接");//非离线模式，状态设置为未连接
    }

    void Update()//每帧调用
    {
        var pending = _statusPending;
        if (pending != null)
        {
            _statusPending = null;
            Status = pending;
            StatusChanged?.Invoke(pending);
        }//更新状态触发事件
        int budget = 8;//设定每帧最多处理8条json，防止主线程卡死
        while ((budget--)> 0 && _inbox.TryDequeue(out string json))//从线程队列里面取出消息，交给该函数解析
        {
            HandleMessage(json);
        }

        ApplyLegs();
        ApplyBody();//更新腿和机身

        // 键盘只用于"遥控真狗"；离线模拟时由 DogSimulator 自己处理按键
        if (enableKeyboardDebug && Connected) HandleDebugKeys();
    }
    void HandleMessage(string json)//处理消息函数
    {
        if (logMessages) Debug.Log("[caiyu] " + json);//打印原始json，体哦啊是用

        // 延迟探测的应答要先处理（它在 telemetry 过滤之前，否则会被丢掉）
        if (json.IndexOf("\"pong\"", StringComparison.Ordinal) >= 0)//先处理是不是pong延迟应答包，二进制比对，看是否有pong
        {
            HandlePong();
            return;//心跳包，用来查看是否还在连接
        }
        // 快速过滤：只有 telemetry 需要逐帧解析，这个是用来解析狗的运动
        if (json.IndexOf("\"telemetry\"", StringComparison.Ordinal) < 0) return;//同理
        DogTelemetry t;
        try { t = JsonUtility.FromJson<DogTelemetry>(json); }//尝试接受json文件
        catch (Exception e) { Debug.LogWarning("[caiyu] 解析失败: " + e.Message); return; }
        if (t == null || t.type != "telemetry") return;
        ApplyTelemetry(t);
    }
    /// <summary>
    /// 离线模拟入口：DogSimulator 直接塞一帧假数据进来，跳过 JSON 序列化，
    /// 后面走的完全是和真机一样的通路（腿、身体、灯光、事件）。
    /// </summary>
    public void InjectTelemetry(DogTelemetry t)
    {
        if (t == null) return;
        if (string.IsNullOrEmpty(t.type)) t.type = "telemetry";
        ApplyTelemetry(t);
    }

    void ApplyTelemetry(DogTelemetry t)
    {
        Latest = t;//保存到这个只读里面

        if (t.action != _lastAction)//对比上一帧的动作，不同往下走
        {
            _lastAction = t.action;
            ActionChanged?.Invoke(t.action);
        }
        if (t.emotion != _lastEmotion)//对比上一帧的表情
        {
            _lastEmotion = t.emotion;
            EmotionChanged?.Invoke(t.emotion);
        }

        for (int i = 0; i < 4; ++i)//循环四条腿，速出目标旋转角度，逻辑角度，存到数组中
        {
            _legTarget[i] = TargetDegrees(t, i);
            _legLogical[i] = LogicalDegrees(t, i);
        }
        _legsInitialized = true;//标记收到遥测

        ApplyLamp(t.lamp);//更新灯带
        TelemetryReceived?.Invoke(t);//灯带事件
    }

   //用来转换为unity模型骨骼用的逻辑旋转角，在硬件中90°代表中立，在unity中0°代表中立，所以最后要减去90度；
    float LogicalDegrees(DogTelemetry t, int index)
    {
        int raw = (t.legs != null && t.legs.Length > index) ? t.legs[index] : 90;//从遥测包里面拿到这条腿的初始角度
        int trim = (t.trims != null && t.trims.Length > index) ? t.trims[index] : 0;//拿到校准微调角度。
        return raw + trim - 90f;
    }

   //模型转到目标角度，就要用换算出来的逻辑角
    float TargetDegrees(DogTelemetry t, int index) => LogicalDegrees(t, index);

    /// <summary>第 index 条腿的逻辑角偏差（度），顺序 LF, RF, LB, RB。</summary>
    public float LogicalDeg(int index) => (index >= 0 && index < 4) ? _legLogical[index] : 0f;//读取腿的逻辑角
    void ApplyLegs()
    {
        if (!_legsInitialized) return;//没收到遥测直接退出
        if (!_restCaptured) CaptureRestPose();//采集静态信息
        for (int i = 0; i < 4 && i < legRoots.Length; ++i)
        {
            if (legRoots[i] == null) continue;
            _legCurrent[i] = Mathf.LerpAngle(_legCurrent[i], _legTarget[i], legSmoothing);//按平滑插值向目标角度靠近
            // 先套用美术模型自带的静止姿态，再叠加上角度偏差
            legRoots[i].localRotation = _restRot[i] * Quaternion.AngleAxis(_legCurrent[i], _legAxisLocal[i]);//静态姿态*摆动旋转，angleaxis（角度，轴）
            //这句话代表的含义先让他，旋转到自己的静止姿态，之后在根据一个轴进行摆动。可以用手臂进行模拟。
        }
    }
    //捕获静止姿态
    public void CaptureRestPose()//这个是用来存储四条腿站直不动的时候本来的旋转样子，同时算好每条腿的摆动轴向
    {
        for (int i = 0; i < 4; ++i)
        {
            Transform leg = (legRoots != null && i < legRoots.Length) ? legRoots[i] : null;//取出第i条腿的骨骼对象
            if (leg == null)//如果为空
            {
                _restRot[i] = Quaternion.identity;//将他基准旋转设置为无旋转
                _legAxisLocal[i] = legRotationAxis;//摆动轴就是原始摆动轴
                continue;
            }//读取当前髋关节的lcoalRotation，作为静止姿态保存

            _restRot[i] = leg.localRotation;//保存这条腿的基础旋转，相对于父骨骼的旋转

            // legRotationAxis 按车体空间理解 → 转到骨骼父级空间 → 再转到骨骼本地空间
            //理解四个空间1.机身本地空间：脚本挂载狗身上的transform，legRotationAxis代表相对于狗身子向规定的方向摆动
            //2.世界空间，unity这个空间的坐标系
            //3.父骨骼空间：大腿的父物体自己的局部坐标
            //4.大腿骨骼本地空间：大腿骨骼自己的坐标系
            Vector3 axisParent = leg.parent != null? leg.parent.InverseTransformDirection(transform.TransformDirection(legRotationAxis)): legRotationAxis;
            //transformDirection:本地坐标到世界坐标，意义就是将自己的坐标换算成世界坐标系真正的朝向，之后inverseTransformDirection将世界方向转为父骨骼本地方向
            //也就是换算成在父骨骼这个轴的朝向，因为腿的骨骼是相对于它的父物体旋转的
            Vector3 axisLocal = Quaternion.Inverse(_restRot[i]) * axisParent;//得到腿在自己本地坐标系里，摆动轴的方向
            _legAxisLocal[i] = axisLocal.sqrMagnitude < 1e-6f ? legRotationAxis : axisLocal.normalized;//保底，安全检测
        }

        _restCaptured = true;
    }

    [ContextMenu("重新记录腿部静止姿态")]
    void CaptureRestPoseFromMenu() => CaptureRestPose();

    void ApplyBody()
    {
        if (body == null || Latest == null || bodyTiltGain <= 0f) return;

        float front = (Raw(0) + Raw(1)) * 0.5f;
        float back = (Raw(2) + Raw(3)) * 0.5f;
        float left = (Raw(0) + Raw(2)) * 0.5f;
        float right = (Raw(1) + Raw(3)) * 0.5f;

        float pitchTarget = (back - front) * bodyTiltGain;
        float rollTarget = (left - right) * bodyTiltGain;

        _bodyPitch = Mathf.Lerp(_bodyPitch, pitchTarget, bodySmoothing);
        _bodyRoll = Mathf.Lerp(_bodyRoll, rollTarget, bodySmoothing);
        body.localRotation = Quaternion.Euler(_bodyPitch, 0f, _bodyRoll);
    }

    float Raw(int index)
    {
        if (Latest?.legs == null || Latest.legs.Length <= index) return 90f;
        return Latest.legs[index];
    }

    void ApplyLamp(DogLamp lamp)
    {
        if (lamp == null || stripRenderers == null) return;

        float scale = lamp.on ? Mathf.Clamp01(lamp.brightness / 100f) : 0f;
        var color = new Color(lamp.r / 255f, lamp.g / 255f, lamp.b / 255f) * (scale * emissionIntensity);

        var block = new MaterialPropertyBlock();
        foreach (var r in stripRenderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(block);
            block.SetColor(emissionColorProperty, color);
            r.SetPropertyBlock(block);
        }
    }

    void HandleDebugKeys()
    {
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) SendDrive("forward");
        if (Input.GetKeyUp(KeyCode.W) || Input.GetKeyUp(KeyCode.UpArrow)) SendDrive("none");
        if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) SendDrive("backward");
        if (Input.GetKeyUp(KeyCode.S) || Input.GetKeyUp(KeyCode.DownArrow)) SendDrive("none");
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) SendDrive("left");
        if (Input.GetKeyUp(KeyCode.A) || Input.GetKeyUp(KeyCode.LeftArrow)) SendDrive("none");
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) SendDrive("right");
        if (Input.GetKeyUp(KeyCode.D) || Input.GetKeyUp(KeyCode.RightArrow)) SendDrive("none");
        if (Input.GetKeyDown(KeyCode.Space)) ToggleChat();
    }

    // ------------------------------------------------------------------ 发送 API

    public void SendAction(string action) => SendJson($"{{\"type\":\"action\",\"action\":{Quote(action)}}}");

    public void SendDrive(string dir) => SendJson($"{{\"type\":\"drive\",\"dir\":{Quote(dir)}}}");

    public void SendEmotion(string emotion) => SendJson($"{{\"type\":\"emotion\",\"emotion\":{Quote(emotion)}}}");

    public void SendSpeed(int percent) => SendJson($"{{\"type\":\"speed\",\"value\":{Mathf.Clamp(percent, 50, 200)}}}");

    public void ToggleChat() => SendJson("{\"type\":\"chat\",\"action\":\"toggle\"}");

    /// <summary>
    /// 灯带指令。action：on / off / breathe / rainbow / flow / flash / brightness / speed / query。
    ///
    /// ⚠ 固件里 brightness 和 speed 读的是 **"value"** 字段（caiyu_web_server.cc:511/513），
    /// 不是 "brightness"。这里两个都发，兼容以后的改名。
    /// </summary>
    public void SendLamp(string action, int r = 255, int g = 255, int b = 255, int value = 80)
        => SendJson($"{{\"type\":\"lamp\",\"action\":{Quote(action)},\"r\":{r},\"g\":{g},\"b\":{b},\"value\":{value},\"brightness\":{value}}}");

    public void SubscribeTelemetry(int hz)
        => SendJson($"{{\"type\":\"telemetry\",\"hz\":{Mathf.Clamp(hz, 1, 30)}}}");

    public void UnsubscribeTelemetry() => SendJson("{\"type\":\"telemetry\",\"enable\":false}");

    /// <summary>只取一帧，不订阅。</summary>
    public void RequestSnapshot() => SendJson("{\"type\":\"snapshot\"}");

    static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\"", string.Empty) + "\"";

    void SendJson(string json) => _outbox.Enqueue(json);

    // ------------------------------------------------------------------ 延迟探测（ping/pong）

    /// <summary>最近一次往返时延（毫秒）。单向延迟通常按 RTT/2 估算。</summary>
    public float LastRttMs { get; private set; }

    /// <summary>全部样本的平均往返时延（毫秒）。</summary>
    public float AvgRttMs { get; private set; }

    /// <summary>收到 pong 时触发，参数是这一次的 RTT（毫秒）。</summary>
    public event Action<float> RttMeasured;

    long _pingSentMs;
    double _rttSum;
    int _rttCount;

    /// <summary>
    /// 发一个 ping。固件原样回传 "t"（见 caiyu_web_server.cc 的 ping 分支），
    /// 所以往返时延可以用本地单调时钟直接算，不需要两边对时。
    /// </summary>
    public void SendPing()
    {
        _pingSentMs = NowMs();
        SendJson($"{{\"type\":\"ping\",\"t\":{_pingSentMs}}}");
    }

    void HandlePong()
    {
        long now = NowMs();
        float rtt = Mathf.Max(0f, (float)(now - _pingSentMs));
        LastRttMs = rtt;

        _rttSum += rtt;
        _rttCount++;
        AvgRttMs = (float)(_rttSum / _rttCount);

        RttMeasured?.Invoke(rtt);
    }

    /// <summary>单调毫秒时钟（不受帧率和系统时间调整影响）</summary>
    static long NowMs() => (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

    /// <summary>
    /// 改完 <see cref="deviceHost"/> 或 <see cref="offlineSimulation"/> 之后调用，
    /// 按新参数重新建连（走的是 Unity 的 OnDisable → OnEnable，最稳）。
    /// 打包成 exe 后，界面上换 IP 靠的就是它。
    /// </summary>
    public void Reconnect()
    {
        if (!enabled) return;
        enabled = false;
        enabled = true;
    }

    void SetStatus(string s)
    {
        if ((_statusPending ?? Status) == s) return;
        _statusPending = s;
    }

    // ------------------------------------------------------------------ 网络

    async Task ConnectionLoop(CancellationToken token)
    {
        int backoffMs = 500;

        while (!token.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            _ws = ws;
            _sendGate = new SemaphoreSlim(1, 1);
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            try
            {
                SetStatus("连接中 " + deviceHost);
                await ws.ConnectAsync(new Uri($"ws://{deviceHost}:{port}/ws"), token);
                SetStatus("已连接");
                backoffMs = 500;

                // 一上来就订阅，并把本地缓存清掉，避免用旧位形插值
                _legsInitialized = false;
                SubscribeTelemetry(telemetryHz);

                Task sendTask = Task.Run(() => SendLoop(ws, connCts.Token));
                await ReceiveLoop(ws, connCts.Token);

                connCts.Cancel();
                try { await sendTask; } catch { /* 关连接时正常 */ }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                SetStatus("连接异常: " + e.Message);
            }
            finally
            {
                try { ws.Dispose(); } catch { }
                _ws = null;
            }

            if (!autoReconnect) break;

            try { await Task.Delay(backoffMs, token); }
            catch { break; }
            backoffMs = Mathf.Min(backoffMs * 2, 8000);
        }

        SetStatus("已断开");
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder(4096);

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SetStatus("设备关闭连接");
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text) continue;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            if (sb.Length > 0) _inbox.Enqueue(sb.ToString());
        }
    }

    async Task SendLoop(ClientWebSocket ws, CancellationToken token)
    {
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (!_outbox.TryDequeue(out string message))
            {
                try { await Task.Delay(10, token); } catch { return; }
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await _sendGate.WaitAsync(token);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }
}
