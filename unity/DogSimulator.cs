// DogSimulator.cs —— 没有硬件时让"狗"在 Unity 里自己动起来
//
// 作用：以 20 Hz 本地生成和真机格式完全一致的姿态快照，喂给 DogClient，
//      于是腿、身体姿态、表情、灯带全都会动 —— 走、退、转、坐、趴、睡、挥手都能演。
//
// 步态表是照抄固件 main/boards/caiyu-c3/caiyu_actionments.cc 的：
//   DoWalk 8 拍（45/90/135）、DoTurn 4 拍（50/90/130），顺序 左前、右前、左后、右后。
//
// 用法：
//   1. DogClient 上勾选 offlineSimulation（不连设备）
//   2. 本脚本挂到狗根节点（或任意物体），client 拖进去
//   3. 进 Play 就能看到它在走；按键 W/A/S/D 前进后退左右转，1-9 切动作

using UnityEngine;

public class DogSimulator : MonoBehaviour
{
    [Header("引用")]
    public DogClient client;

    [Header("开关")]
    public bool playOnStart = true;
    [Tooltip("开局动作：stand / sit / sleep / prone")]
    public string startAction = "stand";
    [Tooltip("没动作这么久就自己趴下睡觉（0 = 不睡）")]
    public float idleSleepSeconds = 8f;

    [Header("节奏")]
    [Range(1, 30)] public int hz = 20;
    public float gaitPeriodMs = 150f;   // 走路一拍
    public float turnPeriodMs = 180f;   // 转向一拍
    public float poseDurationMs = 900f; // 换姿态的过渡时长

    [Header("世界位移（可选）")]
    public bool moveInWorld = true;
    [Tooltip("勾上时 walkSpeed 的单位是「腿长/秒」，模型放大缩小都不用改")]
    public bool speedInLegLengths = true;
    public float walkSpeed = 1.6f;
    public float turnSpeedDeg = 90f;    // 度/秒

    [Header("键盘（模拟模式专用）")]
    public bool enableKeyboard = true;

    [Header("灯带")]
    public Color lampColor = new Color32(255, 107, 53, 255);

    // ---- 与固件一致的步态表：顺序 LF, RF, LB, RB ----
    static readonly int[][] WalkForward =
    {
        new[] { 90, 45, 45, 90 },
        new[] { 135, 45, 45, 135 },
        new[] { 135, 90, 90, 135 },
        new[] { 90, 90, 90, 90 },
        new[] { 45, 90, 90, 45 },
        new[] { 45, 135, 135, 45 },
        new[] { 90, 135, 135, 90 },
        new[] { 90, 90, 90, 90 },
    };

    static readonly int[][] WalkBackward =
    {
        new[] { 90, 135, 135, 90 },
        new[] { 45, 135, 135, 45 },
        new[] { 45, 90, 90, 45 },
        new[] { 90, 90, 90, 90 },
        new[] { 135, 90, 90, 135 },
        new[] { 135, 45, 45, 135 },
        new[] { 90, 45, 45, 90 },
        new[] { 90, 90, 90, 90 },
    };

    static readonly int[][] TurnRight =
    {
        new[] { 90, 130, 50, 90 },
        new[] { 130, 130, 50, 50 },
        new[] { 130, 90, 90, 50 },
        new[] { 90, 90, 90, 90 },
    };

    static readonly int[][] TurnLeft =
    {
        new[] { 130, 90, 90, 50 },
        new[] { 130, 130, 50, 50 },
        new[] { 90, 130, 50, 90 },
        new[] { 90, 90, 90, 90 },
    };

    static readonly int[] PoseStand = { 90, 90, 90, 90 };
    static readonly int[] PoseSit = { 90, 90, 20, 20 };
    static readonly int[] PoseSleep = { 180, 180, 0, 0 };
    static readonly int[] PoseProne = { 0, 0, 180, 180 };

    enum Mode { Pose, Gait, Wave }

    Mode _mode = Mode.Pose;
    readonly float[] _cur = { 90f, 90f, 90f, 90f };
    int[] _fromPose = (int[])PoseStand.Clone();
    int[] _toPose = (int[])PoseStand.Clone();
    float _poseT;

    int[][] _table = WalkForward;
    int _phase;
    float _phaseT;
    int _stepsLeft;

    string _action = "stand";
    string _drive = "none";
    string _emotion = "neutral";
    float _idleTimer;
    float _waveT;
    float _tickAcc;
    float _yaw;
    bool _running;

    // 世界位移用：模型自己的朝向与旋转中心（避免绕物体原点公转）
    DogBodySolver _solver;
    Vector3 _heading = Vector3.forward;
    Vector3 _pivotLocal;
    bool _headingInit;

    // ------------------------------------------------------------------ 生命周期

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (_solver == null) _solver = GetComponent<DogBodySolver>();
        if (client != null && !client.offlineSimulation)
        {
            Debug.LogWarning("[菜鱼狗模拟器] DogClient.offlineSimulation 没勾上，" +
                             "模拟数据和真机数据会互相打架，建议勾上。");
        }
    }

    void Start()
    {
        _running = playOnStart;
        Play(string.IsNullOrEmpty(startAction) ? "stand" : startAction);
        Emit();   // 先给一帧，避免开局是一堆零角度
    }

    void Update()
    {
        if (!_running || client == null) return;

        // 连上真机时必须让位：否则模拟器每帧注入的遥测会和设备数据抢同一个
        // Transform，表现为狗不停抽搐 / 动作错乱。
        if (!client.offlineSimulation) return;

        if (enableKeyboard) HandleKeyboard();

        // 世界位移每帧算，保证在场景里走得顺
        if (moveInWorld) MoveInWorld(Time.deltaTime);

        // 姿态按 hz 节拍更新
        _tickAcc += Time.deltaTime;
        float period = 1f / Mathf.Max(1, hz);
        if (_tickAcc < period) return;

        float dt = _tickAcc;
        _tickAcc = 0f;

        Step(dt);
        Emit();
    }

    // ------------------------------------------------------------------ 对外 API

    /// <summary>播一个动作：stand/sit/sleep/prone/walk/walk_back/turn_left/turn_right/wave</summary>
    public void Play(string action)
    {
        if (string.IsNullOrEmpty(action)) action = "stand";

        _idleTimer = 0f;
        _action = action;

        switch (action)
        {
            case "walk":
                StartGait(WalkForward, 1);
                break;
            case "walk_back":
                StartGait(WalkBackward, 1);
                break;
            case "turn_left":
                StartGait(TurnLeft, 1);
                break;
            case "turn_right":
                StartGait(TurnRight, 1);
                break;
            case "wave":
                StartPose(PoseSit);
                _mode = Mode.Wave;
                _waveT = 0f;
                break;
            case "sit":
                StartPose(PoseSit);
                break;
            case "sleep":
                StartPose(PoseSleep);
                break;
            case "prone":
                StartPose(PoseProne);
                break;
            default:
                StartPose(PoseStand);
                break;
        }

        _emotion = EmotionFor(action);
    }

    /// <summary>持续驱动：forward/backward/left/right/none（模拟摇杆按住）</summary>
    public void SetDrive(string dir)
    {
        if (string.IsNullOrEmpty(dir)) dir = "none";
        if (_drive == dir) return;
        _drive = dir;

        if (dir == "none") return;

        _idleTimer = 0f;
        Play(dir == "forward" ? "walk"
            : dir == "backward" ? "walk_back"
            : dir == "left" ? "turn_left" : "turn_right");
    }

    public void StopDrive() => SetDrive("none");

    public void SetEmotion(string emotion) => _emotion = emotion;

    public void SetRunning(bool running) => _running = running;

    public bool Running => _running;

    // ------------------------------------------------------------------ 状态机

    void StartPose(int[] target)
    {
        _mode = Mode.Pose;
        _fromPose = new int[4];
        _toPose = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            _fromPose[i] = Mathf.RoundToInt(_cur[i]);
            _toPose[i] = target[i];
        }
        _poseT = 0f;
    }

    void StartGait(int[][] table, int steps)
    {
        _mode = Mode.Gait;
        _table = table;
        _phase = 0;
        _phaseT = 0f;
        _stepsLeft = Mathf.Max(1, steps) * table.Length;   // 按"拍"计数
    }

    void Step(float dt)
    {
        float ms = dt * 1000f;

        switch (_mode)
        {
            case Mode.Pose:
                _poseT += ms / Mathf.Max(1f, poseDurationMs);
                _poseT = Mathf.Clamp01(_poseT);
                for (int i = 0; i < 4; ++i)
                {
                    _cur[i] = Mathf.Lerp(_fromPose[i], _toPose[i], Smooth(_poseT));
                }
                break;

            case Mode.Gait:
            {
                float period = FloatPeriodFor(_table);
                _phaseT += ms;
                while (_phaseT >= period)
                {
                    _phaseT -= period;
                    _phase = (_phase + 1) % _table.Length;
                    _stepsLeft--;
                }

                if (_stepsLeft <= 0 && _drive == "none")
                {
                    // 走完自动回站立（固件里 remote 模式结束会 DoStand）
                    Play("stand");
                    break;
                }

                int next = (_phase + 1) % _table.Length;
                float k = Mathf.Clamp01(_phaseT / period);
                int[] a = _table[_phase];
                int[] b = _table[next];
                for (int i = 0; i < 4; ++i)
                {
                    _cur[i] = Mathf.Lerp(a[i], b[i], k);
                }
                break;
            }

            case Mode.Wave:
                _waveT += ms;
                if (_waveT >= 2400f)
                {
                    Play("stand");
                    break;
                }

                if (_waveT < 400f)
                {
                    // 先花 0.4 s 坐下去，避免"啪"地一跳
                    float k = Smooth(_waveT / 400f);
                    for (int i = 0; i < 4; ++i)
                    {
                        _cur[i] = Mathf.Lerp(_fromPose[i], PoseSit[i], k);
                    }
                }
                else
                {
                    // 坐着，左前腿 0→65→0 摆三次（约 0.8 s 一次）
                    _cur[0] = 32.5f + 32.5f * Mathf.Cos((_waveT - 400f) * 2f * Mathf.PI / 800f);
                    _cur[1] = PoseSit[1];
                    _cur[2] = PoseSit[2];
                    _cur[3] = PoseSit[3];
                }
                break;
        }

        // 空闲计时 → 自动趴下睡觉
        if (idleSleepSeconds > 0f && _drive == "none" && _mode == Mode.Pose &&
            _action != "sleep" && _action != "sit" && _action != "prone")
        {
            _idleTimer += dt;
            if (_idleTimer >= idleSleepSeconds)
            {
                Play("sleep");
            }
        }
    }

    float FloatPeriodFor(int[][] table)
    {
        if (table == TurnLeft || table == TurnRight) return Mathf.Max(1f, turnPeriodMs);
        return Mathf.Max(1f, gaitPeriodMs);
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);

    static string EmotionFor(string action)
    {
        switch (action)
        {
            case "sleep": return "sleepy";
            case "sit":
            case "prone": return "relaxed";
            case "walk":
            case "walk_back":
            case "turn_left":
            case "turn_right": return "happy";
            case "wave": return "laughing";
            default: return "neutral";
        }
    }

    // ------------------------------------------------------------------ 输出快照

    void Emit()
    {
        var t = new DogTelemetry
        {
            type = "telemetry",
            t = (long)(Time.time * 1000f),
            state = "idle",
            voice = false,
            emotion = _emotion,
            action = _action,
            busy = _mode != Mode.Pose || _poseT < 1f,
            drive = _drive,
            speed = 100,
            legs = new int[4],
            trims = new int[4],
            lamp = new DogLamp
            {
                on = true,
                mode = _drive == "none" ? 2 : 4,   // 2 呼吸 / 4 流水
                brightness = 60,
                speed = 100,
                r = Mathf.Clamp(Mathf.RoundToInt(lampColor.r * 255f), 0, 255),
                g = Mathf.Clamp(Mathf.RoundToInt(lampColor.g * 255f), 0, 255),
                b = Mathf.Clamp(Mathf.RoundToInt(lampColor.b * 255f), 0, 255),
            },
        };

        for (int i = 0; i < 4; ++i)
        {
            t.legs[i] = Mathf.Clamp(Mathf.RoundToInt(_cur[i]), 0, 180);
            t.trims[i] = 0;
        }

        client.InjectTelemetry(t);
    }

    // ------------------------------------------------------------------ 世界位移 & 键盘

    void MoveInWorld(float dt)
    {
        if (!_headingInit)
        {
            _heading = DogPivot.ForwardOf(transform, _solver);
            _pivotLocal = DogPivot.ComputePivotLocal(transform);
            _headingInit = true;
        }

        float legLen = (_solver != null && _solver.legLength > 1e-6f) ? _solver.legLength : 1f;
        float unitsPerSec = speedInLegLengths ? walkSpeed * legLen : walkSpeed;

        float advance = 0f;
        float yawDelta = 0f;

        switch (_drive)
        {
            case "forward": advance = 1f; break;
            case "backward": advance = -1f; break;
            case "left": yawDelta = -turnSpeedDeg * dt; break;
            case "right": yawDelta = turnSpeedDeg * dt; break;
        }

        if (yawDelta != 0f)
        {
            // 绕模型自己的可视中心转（不是绕物体原点），否则会公转
            _yaw += yawDelta;
            DogPivot.Spin(transform, _pivotLocal, yawDelta);
            _heading = Quaternion.AngleAxis(yawDelta, Vector3.up) * _heading;
            _heading = DogPivot.Horizontal(_heading, Vector3.forward);
        }

        if (advance != 0f)
        {
            transform.position += _heading * (advance * unitsPerSec * dt);
        }
    }

    void HandleKeyboard()
    {
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) SetDrive("forward");
        if (Input.GetKeyUp(KeyCode.W) || Input.GetKeyUp(KeyCode.UpArrow)) SetDrive("none");
        if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) SetDrive("backward");
        if (Input.GetKeyUp(KeyCode.S) || Input.GetKeyUp(KeyCode.DownArrow)) SetDrive("none");
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) SetDrive("left");
        if (Input.GetKeyUp(KeyCode.A) || Input.GetKeyUp(KeyCode.LeftArrow)) SetDrive("none");
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) SetDrive("right");
        if (Input.GetKeyUp(KeyCode.D) || Input.GetKeyUp(KeyCode.RightArrow)) SetDrive("none");

        if (Input.GetKeyDown(KeyCode.Alpha1)) Play("stand");
        if (Input.GetKeyDown(KeyCode.Alpha2)) Play("sit");
        if (Input.GetKeyDown(KeyCode.Alpha3)) Play("sleep");
        if (Input.GetKeyDown(KeyCode.Alpha4)) Play("prone");
        if (Input.GetKeyDown(KeyCode.Alpha5)) Play("wave");
        if (Input.GetKeyDown(KeyCode.Alpha6)) Play("walk");
        if (Input.GetKeyDown(KeyCode.Alpha7)) Play("walk_back");
        if (Input.GetKeyDown(KeyCode.Alpha8)) Play("turn_left");
        if (Input.GetKeyDown(KeyCode.Alpha9)) Play("turn_right");

        if (Input.GetKeyDown(KeyCode.Q)) SetEmotion("happy");
        if (Input.GetKeyDown(KeyCode.E)) SetEmotion("sleepy");
        if (Input.GetKeyDown(KeyCode.R)) SetEmotion("surprised");
        if (Input.GetKeyDown(KeyCode.F)) SetEmotion("loving");
    }
}
