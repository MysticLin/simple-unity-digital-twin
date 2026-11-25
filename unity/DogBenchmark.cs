// DogBenchmark.cs —— 数字孪生的量化评估（论文实验数据来源）
//
// 测什么、为什么这么测：
//
// 1) 端到端延迟
//    设备时间戳 t 是"设备开机毫秒数"，和 Unity 的时钟不是同一个时间基准，
//    所以**不能**直接相减。做法是：用 ping/pong 测往返时延 RTT（固件原样回传
//    我们发的 t，见 caiyu_web_server.cc 的 ping 分支），单向延迟按 RTT/2 估算。
//
// 2) 到达抖动与丢帧
//    设备按 20Hz（50ms）推遥测。收到两帧的到达间隔如果明显大于 50ms，
//    多出来的部分就是丢帧或网络重传。用 round(interval/period)-1 估算丢了几帧。
//
// 3) 同步误差（最核心的指标）
//    设备推的 legs 是**指令角**（舵机 240°/s 斜坡的目标值，不是编码器实测）。
//    Unity 模型把腿旋转 actual = Angle(静止姿态, 当前姿态)。
//    理想情况 actual 应该等于 |指令角偏差| = |legs[i] + trims[i] - 90|。
//    两者之差就是"孪生体与真机的同步误差"。
//    注意：运动过程中客户端有平滑（legSmoothing），会天然产生滞后，
//    所以额外统计"静止期"（指令角连续 3 帧不变）的误差，那才是真实同步精度。
//
// 用法：挂在 CaiyuDog 上（client / solver / 四条腿自动找），Play 后自动开始，
//       durationSec 到了自动停并把 CSV 写到 persistentDataPath。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public class DogBenchmark : MonoBehaviour
{
    [Header("引用（可留空自动找）")]
    public DogClient client;
    public DogBodySolver solver;
    public Transform[] legs = new Transform[4];

    [Header("运行")]
    public bool autoStart = true;
    [Tooltip("测量时长（秒），0 = 一直测")]
    public float durationSec = 60f;
    [Tooltip("设备标称推送频率，用于估算丢帧")]
    public float expectedHz = 20f;
    [Tooltip("ping 间隔（秒），0 = 不发 ping（就测不了 RTT）")]
    public float pingIntervalSec = 1f;
    [Tooltip("开测前丢弃的前 N 帧（避开开局插值追赶的瞬态）。0 = 不丢")]
    public int warmupFrames = 20;
    [Tooltip("指令角连续 N 帧不变才算「静止期」。慢速运动时 1 帧会误判，建议 3")]
    public int stableFramesRequired = 3;
    [Tooltip("测量期间限制帧率（0 = 不限）。做帧率对比实验时用它固定 30/60/120")]
    public int frameRateCap = 0;

    [Header("导出")]
    public bool exportOnStop = true;
    public string exportFolder = "";      // 留空 = Application.persistentDataPath
    [Tooltip("是否导出逐帧明细（不勾只导出汇总）")]
    public bool exportFrames = true;

    // ---- 对外状态 ----
    public bool Running { get; private set; }
    public string Summary { get; private set; } = "未开始";

    // ---- 原始数据 ----
    readonly List<Frame> _frames = new List<Frame>();
    readonly List<float> _rtts = new List<float>();
    readonly Quaternion[] _restLocal = new Quaternion[4];

    // ---- 累计 ----
    float _elapsed;
    float _pingAcc;
    float _fpsAcc;
    int _fpsFrames;
    long _lastDeviceT = long.MinValue;
    float _lastArrivalMs = -1f;
    int _stableRun;          // 指令角连续不变的帧数（用于静止期判定）

    struct Frame
    {
        public long deviceT;
        public float arrivalMs;
        public float intervalMs;
        public int lost;
        public int[] phi;       // 设备指令偏差角
        public float[] actual;  // 模型实际旋转角
        public float[] error;   // |actual| - |phi|
        public bool stable;     // 四腿指令角都未变化
        public float rtt;
    }

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (solver == null) solver = GetComponent<DogBodySolver>();
        if (solver != null && (legs == null || legs.Length < 4 || legs[0] == null)) legs = solver.hips;

        // 必须在客户端开始驱动之前记录静止姿态
        if (legs != null && legs.Length >= 4)
        {
            for (int i = 0; i < 4; ++i)
                if (legs[i] != null) _restLocal[i] = legs[i].localRotation;
        }
    }

    void Start()
    {
        if (autoStart) StartMeasure();
    }

    void Update()
    {
        if (!Running) return;

        _elapsed += Time.deltaTime;
        _fpsAcc += Time.deltaTime;
        _fpsFrames++;

        if (durationSec > 0f && _elapsed >= durationSec) StopMeasure();

        if (pingIntervalSec > 0f && client != null && client.Connected)
        {
            _pingAcc += Time.deltaTime;
            if (_pingAcc >= pingIntervalSec)
            {
                _pingAcc = 0f;
                client.SendPing();
            }
        }
    }

    // ------------------------------------------------------------------ 控制

    public void StartMeasure()
    {
        _frames.Clear();
        _rtts.Clear();
        _elapsed = 0f;
        _pingAcc = 0f;
        _fpsAcc = 0f;
        _fpsFrames = 0;
        _lastDeviceT = long.MinValue;
        _lastArrivalMs = -1f;
        _stableRun = 0;

        // 帧率对比实验：固定帧率，保证三次实验可比
        // 注意：frameRateCap = 0 时会显式设成 -1（不限帧），
        // 否则上一次限帧实验设的 60 会一直留着，导致"不限帧"那组其实也被限到 60。
        if (frameRateCap > 0)
        {
            Application.targetFrameRate = frameRateCap;
            QualitySettings.vSyncCount = 0;
            Debug.Log($"[基准测试] 帧率限制为 {frameRateCap}（VSync 已关）");
        }
        else
        {
            Application.targetFrameRate = -1;
            Debug.Log("[基准测试] 帧率不限（targetFrameRate = -1）");
        }

        if (client != null) client.RttMeasured += OnRtt;
        Running = true;
        Summary = "测量中…";
        Debug.Log("[基准测试] 开始");
    }

    public void StopMeasure()
    {
        if (!Running) return;
        Running = false;
        if (client != null) client.RttMeasured -= OnRtt;

        Summary = BuildSummary();
        Debug.Log("[基准测试] 结束\n" + Summary);

        if (exportOnStop) ExportCsv();
    }

    void OnRtt(float rtt) => _rtts.Add(rtt);

    // ------------------------------------------------------------------ 采样（由客户端回调）

    void OnEnable()
    {
        if (client != null) client.TelemetryReceived += OnTelemetry;
    }

    void OnDisable()
    {
        if (client != null) client.TelemetryReceived -= OnTelemetry;
        if (Running) StopMeasure();
    }

    void OnTelemetry(DogTelemetry t)
    {
        if (!Running || t == null || t.legs == null || t.legs.Length < 4) return;

        float arrival = (float)(Time.realtimeSinceStartupAsDouble * 1000.0);
        float period = 1000f / Mathf.Max(1f, expectedHz);

        float interval = _lastArrivalMs < 0f ? 0f : (arrival - _lastArrivalMs);
        int lost = _lastArrivalMs < 0f ? 0 : Mathf.Max(0, Mathf.RoundToInt(interval / period) - 1);
        _lastArrivalMs = arrival;
        _lastDeviceT = t.t;

        var phi = new int[4];
        var actual = new float[4];
        var error = new float[4];

        for (int i = 0; i < 4; ++i)
        {
            int trim = (t.trims != null && t.trims.Length > i) ? t.trims[i] : 0;
            phi[i] = t.legs[i] + trim - 90;

            if (legs != null && legs.Length > i && legs[i] != null)
            {
                actual[i] = Quaternion.Angle(_restLocal[i], legs[i].localRotation);
                error[i] = actual[i] - Mathf.Abs(phi[i]);
            }
        }

        // 静止期判定：四腿指令角连续 stableFramesRequired 帧完全一致才算静止。
        // 只比一帧是不够的 —— 慢速运动时整数角度常连续两帧相同，会被误判成静止期。
        int sameCount = 0;
        if (_frames.Count > 0)
        {
            int[] prev = _frames[_frames.Count - 1].phi;
            bool sameAsPrev = true;
            for (int i = 0; i < 4; ++i)
            {
                if (prev[i] != phi[i]) { sameAsPrev = false; break; }
            }
            sameCount = sameAsPrev ? (_stableRun + 1) : 0;
        }
        _stableRun = sameCount;
        bool stable = sameCount >= Mathf.Max(1, stableFramesRequired);

        _frames.Add(new Frame
        {
            deviceT = t.t,
            arrivalMs = arrival,
            intervalMs = interval,
            lost = lost,
            phi = phi,
            actual = actual,
            error = error,
            stable = stable,
            rtt = client != null ? client.LastRttMs : 0f,
        });
    }

    // ------------------------------------------------------------------ 统计

    string BuildSummary()
    {
        int n = _frames.Count;
        var sb = new StringBuilder();
        sb.AppendLine("===== 数字孪生同步基准 =====");
        sb.AppendLine($"帧数: {n}   时长: {_elapsed:F1}s   平均帧率: {(_elapsed > 0 ? _fpsFrames / _elapsed : 0f):F1} FPS");
        if (n == 0) { sb.AppendLine("没有收到任何遥测（检查设备连接）"); return sb.ToString(); }

        // 开局 warmupFrames 帧是"插值从静止姿态追赶"的瞬态，
        // 那段误差可以到几十度（比如上手就是睡姿），算进去会把"最大误差"污染成 90°。
        int start = Mathf.Clamp(warmupFrames, 0, Mathf.Max(0, n - 1));
        int m = n - start;
        sb.AppendLine($"(已跳过前 {start} 帧预热，统计 {m} 帧)");

        // 到达间隔：报告分位数，比"丢帧数"更客观。
        // 注意：WebSocket 走 TCP，**不会丢包**，所谓"丢帧"其实是"迟到造成的空洞"。
        int lostTotal = 0;
        var ivs = new List<float>();
        float ivMax = 0f;
        for (int i = start; i < n; ++i)
        {
            lostTotal += _frames[i].lost;
            if (i > start)
            {
                ivs.Add(_frames[i].intervalMs);
                ivMax = Mathf.Max(ivMax, _frames[i].intervalMs);
            }
        }
        ivs.Sort();
        float lossRate = (m + lostTotal) > 0 ? 100f * lostTotal / (m + lostTotal) : 0f;
        sb.AppendLine($"到达间隔: p50 {Pct(ivs, 0.50f):F1} / p95 {Pct(ivs, 0.95f):F1} / p99 {Pct(ivs, 0.99f):F1} / max {ivMax:F1} ms");
        sb.AppendLine($"时间空洞（TCP 不会丢包，这是迟到造成的）: {lostTotal} 次 / {lossRate:F2}%");

        // RTT
        if (_rtts.Count > 0)
        {
            var rs = new List<float>(_rtts);
            rs.Sort();
            float sum = 0f;
            foreach (float r in rs) sum += r;
            sb.AppendLine($"RTT: p50 {Pct(rs, 0.50f):F1} / p95 {Pct(rs, 0.95f):F1} / max {rs[rs.Count - 1]:F1} ms  → 单向约 {sum / rs.Count / 2f:F1} ms");
        }
        else sb.AppendLine("RTT: 无样本（pingIntervalSec=0 或未连接）");

        // 同步误差：全体 + 静止期
        sb.AppendLine("同步误差（|模型实际角度| − |设备指令角|，单位: 度）");
        sb.AppendLine("        全体均值   全体最大   静止期均值   静止期最大   静止期帧数");
        for (int i = 0; i < 4; ++i)
        {
            float sumAll = 0f, maxAll = 0f, sumStable = 0f, maxStable = 0f;
            int nStable = 0;
            for (int f = start; f < n; ++f)
            {
                float e = Mathf.Abs(_frames[f].error[i]);
                sumAll += e;
                maxAll = Mathf.Max(maxAll, e);
                if (_frames[f].stable) { sumStable += e; maxStable = Mathf.Max(maxStable, e); nStable++; }
            }
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}  {1,9:F2}  {2,9:F2}  {3,10:F2}  {4,10:F2}  {5,10}",
                Names[i], m > 0 ? sumAll / m : 0f, maxAll,
                nStable > 0 ? sumStable / nStable : 0f, maxStable, nStable));
        }

        return sb.ToString();
    }

    /// <summary>线性插值求分位数（list 必须已排序）</summary>
    static float Pct(List<float> sorted, float p)
    {
        if (sorted == null || sorted.Count == 0) return 0f;
        if (sorted.Count == 1) return sorted[0];
        float pos = Mathf.Clamp01(p) * (sorted.Count - 1);
        int lo = Mathf.FloorToInt(pos);
        int hi = Mathf.Min(lo + 1, sorted.Count - 1);
        return Mathf.Lerp(sorted[lo], sorted[hi], pos - lo);
    }

    static readonly string[] Names = { "LF", "RF", "LB", "RB" };

    // ------------------------------------------------------------------ 导出

    public void ExportCsv()
    {
        try
        {
            string dir = string.IsNullOrEmpty(exportFolder) ? Application.persistentDataPath : exportFolder;
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            string summaryPath = Path.Combine(dir, "caiyu-bench-" + stamp + ".txt");
            File.WriteAllText(summaryPath, Summary, Encoding.UTF8);
            Debug.Log("[基准测试] 汇总已导出 " + summaryPath);

            if (!exportFrames) return;

            string csvPath = Path.Combine(dir, "caiyu-bench-" + stamp + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("frame,device_t_ms,arrival_ms,interval_ms,lost,rtt_ms,stable," +
                          "phi_LF,phi_RF,phi_LB,phi_RB,act_LF,act_RF,act_LB,act_RB," +
                          "err_LF,err_RF,err_LB,err_RB");

            for (int f = 0; f < _frames.Count; ++f)
            {
                Frame fr = _frames[f];
                sb.Append(f).Append(',')
                  .Append(fr.deviceT).Append(',')
                  .Append(fr.arrivalMs.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(fr.intervalMs.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(fr.lost).Append(',')
                  .Append(fr.rtt.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(fr.stable ? 1 : 0).Append(',');

                for (int i = 0; i < 4; ++i) sb.Append(fr.phi[i]).Append(',');
                for (int i = 0; i < 4; ++i) sb.Append(fr.actual[i].ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                for (int i = 0; i < 4; ++i)
                {
                    sb.Append(fr.error[i].ToString("F2", CultureInfo.InvariantCulture));
                    if (i < 3) sb.Append(',');
                }
                sb.AppendLine();
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
            Debug.Log("[基准测试] 明细已导出 " + csvPath);
        }
        catch (Exception e)
        {
            Debug.LogError("[基准测试] 导出失败: " + e.Message);
        }
    }
}
