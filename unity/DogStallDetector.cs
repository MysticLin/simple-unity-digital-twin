// DogStallDetector.cs —— 舵机/机构异常检测（数字孪生的"偏差监测"应用）
//
// ============================ 先说清硬件边界 ============================
// 本项目的 legs 来自固件 Actionments::leg_angle()，那是**指令角**（舵机按 240°/s
// 斜坡追的目标值），不是编码器实测。实物上既没有编码器，也没有电流采样，
// 所以"舵机堵转"在物理上**无法直接测量**。
//
// 于是这里检测的是四类**可观测**的异常（它们都是堵转/卡死的外在表现）：
//
//   ① 动作卡住   busy=true 但四条腿指令角长时间不变
//                → 固件内部动作序列推进不下去（常见于供电不足、舵机被卡住、
//                  某条腿到不了目标位导致等待超时）
//   ② 动作超时   busy=true 持续超过 maxActionSeconds
//                → 动作永远结束不了，通常是机械干涉或舵机无力
//   ③ 同步失配   Unity 模型实际角度与设备指令角偏差超过阈值并持续
//                → 孪生体与真机不同步（客户端丢帧、卡顿、或模型绑定被改坏）
//   ④ 通讯中断   超过 noTelemetrySeconds 没收到遥测
//                → WiFi 掉线 / 设备重启 / IP 变了
//
// 要做**真正的**堵转检测（论文里可以写成"后续工作"），需要加一路硬件反馈：
//   · 舵机电源串一个 INA219 / ACS712 采样电流：电流突增 + 角度不前进 = 堵转
//   · 或换用带位置反馈的舵机（如串行总线舵机），直接读实际角度
// 这两种方案的接口都可以复用本脚本的 Alarm 回调，不需要改上层。
// =====================================================================
//
// 用法：挂在 CaiyuDog 上，Play 后自动运行。Console 出现 ⚠ 就是检测到异常。
//       也可以在面板里读 detector.Status 显示状态。

using System;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public class DogStallDetector : MonoBehaviour
{
    public enum Kind
    {
        None = 0,
        Stuck,          // ① 动作卡住
        ActionTimeout,  // ② 动作超时
        SyncMismatch,   // ③ 同步失配
        LinkLost,       // ④ 通讯中断
    }

    [Header("引用（可留空自动找）")]
    public DogClient client;
    public DogBodySolver solver;
    public Transform[] legs = new Transform[4];

    [Header("阈值")]
    [Tooltip("busy 且指令角不变多久算卡住（秒）")]
    public float stuckSeconds = 3f;
    [Tooltip("busy 持续多久算动作超时（秒）")]
    public float maxActionSeconds = 15f;
    [Tooltip("模型与指令角的允许偏差（度）")]
    public float syncToleranceDeg = 20f;
    [Tooltip("偏差持续多久才算异常（秒），避免插值滞后误报")]
    public float syncHoldSeconds = 1.5f;
    [Tooltip("多久没收到遥测算通讯中断（秒）")]
    public float noTelemetrySeconds = 3f;
    [Tooltip("同类告警的最小间隔（秒），防止刷屏")]
    public float alarmCooldownSec = 5f;

    [Header("处置")]
    [Tooltip("检测到卡住/超时时，自动发一次「站立」让它脱离异常姿态")]
    public bool autoRecoverToStand = false;

    [Header("输出")]
    public bool logToConsole = true;

    /// <summary>状态摘要，可给控制面板显示。</summary>
    public string Status { get; private set; } = "正常";

    /// <summary>任何告警都会触发：(类型, 说明)。</summary>
    public event Action<Kind, string> Alarm;

    public bool HasAlarm => _lastAlarmTime >= 0f && (Time.realtimeSinceStartup - _lastAlarmTime) < 5f;

    // ---- 内部状态 ----
    readonly Quaternion[] _restLocal = new Quaternion[4];
    bool _restCaptured;
    float _sinceTelemetry;
    float _busyTime;
    float _stuckTime;
    float _syncViolationTime;
    float _lastAlarmTime = -1f;
    readonly float[] _lastAlarmAt = new float[5];
    readonly int[] _alarmCount = new int[5];

    long _lastFrameT = long.MinValue;
    int[] _lastPhi = new int[4];
    bool _haveLastPhi;

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (solver == null) solver = GetComponent<DogBodySolver>();
        if (solver != null && (legs == null || legs.Length < 4 || legs[0] == null)) legs = solver.hips;

        for (int i = 0; i < 5; ++i) _lastAlarmAt[i] = -999f;
    }

    void Start() => CaptureRest();

    void CaptureRest()
    {
        if (legs == null || legs.Length < 4) return;
        for (int i = 0; i < 4; ++i)
            if (legs[i] != null) _restLocal[i] = legs[i].localRotation;
        _restCaptured = true;
    }

    void Update()
    {
        if (client == null) return;

        // ④ 通讯中断
        _sinceTelemetry += Time.deltaTime;
        if (_sinceTelemetry > noTelemetrySeconds)
        {
            Raise(Kind.LinkLost, $"{_sinceTelemetry:F1}s 没收到遥测（检查 WiFi / IP / 设备是否重启）");
            Status = "通讯中断";
            return;
        }
        if (!client.Connected)
        {
            Status = "未连接";
            return;
        }

        DogTelemetry t = client.Latest;
        if (t == null || t.legs == null || t.legs.Length < 4) return;

        // 每一帧新遥测才重算（用设备时间戳判重）
        bool newFrame = t.t != _lastFrameT;
        if (newFrame)
        {
            _lastFrameT = t.t;
            _sinceTelemetry = 0f;

            // ① 卡住判定：busy 时指令角是否还在变
            bool moved = !_haveLastPhi;
            for (int i = 0; i < 4; ++i)
            {
                int phi = t.legs[i] + ((t.trims != null && t.trims.Length > i) ? t.trims[i] : 0) - 90;
                if (!_haveLastPhi || phi != _lastPhi[i]) moved = true;
                _lastPhi[i] = phi;
            }
            _haveLastPhi = true;

            if (t.busy)
            {
                if (!moved) _stuckTime += Time.deltaTime;
                else _stuckTime = 0f;
            }
            else
            {
                _stuckTime = 0f;
            }

            // ③ 同步失配
            if (_restCaptured && legs != null && legs.Length >= 4)
            {
                float worst = 0f;
                for (int i = 0; i < 4; ++i)
                {
                    if (legs[i] == null) continue;
                    float actual = Quaternion.Angle(_restLocal[i], legs[i].localRotation);
                    float expected = Mathf.Abs((float)_lastPhi[i]);
                    worst = Mathf.Max(worst, Mathf.Abs(actual - expected));
                }
                if (worst > syncToleranceDeg) _syncViolationTime += Time.deltaTime;
                else _syncViolationTime = 0f;
            }
        }

        // ② 动作超时
        if (t.busy) _busyTime += Time.deltaTime;
        else _busyTime = 0f;

        // ---- 判定 ----
        if (t.busy && _stuckTime > stuckSeconds)
        {
            Raise(Kind.Stuck,
                $"动作「{t.action}」卡住了 {_stuckTime:F1}s（busy=true 但腿不动）。" +
                "常见原因：舵机供电不足、机械干涉、某条腿被卡。");
            Status = "动作卡住";
        }
        else if (_busyTime > maxActionSeconds)
        {
            Raise(Kind.ActionTimeout,
                $"动作「{t.action}」已经忙了 {_busyTime:F1}s 还没结束（上限 {maxActionSeconds:F0}s）。");
            Status = "动作超时";
        }
        else if (_syncViolationTime > syncHoldSeconds)
        {
            Raise(Kind.SyncMismatch,
                $"孪生体与真机偏差 {_syncViolationTime:F1}s 超过 {syncToleranceDeg:F0}°（渲染卡顿或模型绑定异常）。");
            Status = "同步失配";
        }
        else
        {
            Status = t.busy ? "动作中" : "正常";
        }
    }

    void Raise(Kind kind, string message, bool recover = true)
    {
        int idx = (int)kind;
        if (idx < 0 || idx >= _lastAlarmAt.Length) return;

        float now = Time.realtimeSinceStartup;
        if (now - _lastAlarmAt[idx] < alarmCooldownSec) return;   // 冷却，避免刷屏
        _lastAlarmAt[idx] = now;
        _alarmCount[idx]++;
        _lastAlarmTime = now;

        if (logToConsole) Debug.LogWarning($"[异常检测] {kind}: {message}");
        Alarm?.Invoke(kind, message);

        // 自动处置：只在"卡住/超时"时把狗放回站立，避免它保持怪异姿态
        if (recover && autoRecoverToStand && (kind == Kind.Stuck || kind == Kind.ActionTimeout))
        {
            client.SendAction("stand");
            if (logToConsole) Debug.Log("[异常检测] 已自动发送「站立」尝试脱离异常姿态");
        }
    }

    /// <summary>各类告警的累计次数（按 Kind 索引），论文里可以直接当统计表。</summary>
    public int AlarmCount(Kind kind)
    {
        int i = (int)kind;
        return (i >= 0 && i < _alarmCount.Length) ? _alarmCount[i] : 0;
    }

    /// <summary>生成一份可打印的统计。</summary>
    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== 异常检测统计 =====");
        sb.AppendLine($"动作卡住: {AlarmCount(Kind.Stuck)} 次");
        sb.AppendLine($"动作超时: {AlarmCount(Kind.ActionTimeout)} 次");
        sb.AppendLine($"同步失配: {AlarmCount(Kind.SyncMismatch)} 次");
        sb.AppendLine($"通讯中断: {AlarmCount(Kind.LinkLost)} 次");
        sb.AppendLine($"当前状态: {Status}");
        return sb.ToString();
    }
}
