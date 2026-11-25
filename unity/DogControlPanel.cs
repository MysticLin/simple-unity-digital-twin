// DogControlPanel.cs —— Unity 里直接操控真狗的面板（OnGUI，无需搭 Canvas）
//
// 用途：在 Game 视图左上角显示一组按钮，点一下就通过 WebSocket 给真狗发指令。
// 位置：挂在任意物体上（建议挂 CaiyuDog），把 DogClient 拖进 client。
//
// 注意：
//   · 只能取消 offlineSimulation（连着真机）时才有意义
//   · 摇杆类指令是"点一次生效、一直保持"，所以要停下来必须点「■ 停」
//     （真狗的 drive 会一直保持到下次指令）

using UnityEngine;

[DisallowMultipleComponent]
public class DogControlPanel : MonoBehaviour
{
    [Header("引用")]
    public DogClient client;

    [Header("界面")]
    public bool visible = true;
    public Rect panelRect = new Rect(12f, 12f, 268f, 660f);
    [Tooltip("面板整体不透明度，0.3~1")]
    [Range(0.3f, 1f)] public float backgroundAlpha = 0.85f;

    // ---- 17 个动作（和固件 kActionTable 一一对应）----
    static readonly string[] ActionIds =
    {
        "stand", "sit", "sleep", "prone", "walk", "walk_back",
        "turn_left", "turn_right", "wave", "stretch", "swing", "scratch",
        "kneel", "front_prone", "sway", "raise_hips", "shake_back_legs",
    };

    static readonly string[] ActionLabels =
    {
        "站立", "坐下", "睡觉", "趴下", "前进", "后退",
        "左转", "右转", "挥手", "伸懒腰", "摇摆", "挠痒",
        "跪拜", "前趴", "前后摇", "抬屁股", "伸懒腰2",
    };

    // ---- 摇杆 ----
    static readonly string[] DriveIds = { "forward", "backward", "left", "right", "none" };
    static readonly string[] DriveLabels = { "↑ 前进", "↓ 后退", "← 左转", "→ 右转", "■ 停" };

    // ---- 灯带 ----
    static readonly string[] LampIds = { "on", "off", "breathe", "rainbow", "flow", "flash" };
    static readonly string[] LampLabels = { "开", "关", "呼吸", "彩虹", "流水", "闪烁" };

    float _brightness = 80f;
    float _lampSpeed = 100f;
    float _actionSpeed = 100f;
    Vector2 _scroll;

    // 连接区（打包后靠这个输入框换 IP）
    string _hostInput;
    bool _hostInit;

    // 外观：运行时生成的 1×1 贴图，用来画半透明底板
    Texture2D _bgTex;
    static readonly Color PanelBg = new Color(0.08f, 0.09f, 0.11f, 0.92f);
    static readonly Color HeaderBg = new Color(0.16f, 0.42f, 0.62f, 1f);
    static readonly Color ActionTint = new Color(0.62f, 0.80f, 1.00f, 1f);
    static readonly Color DriveTint = new Color(0.66f, 1.00f, 0.72f, 1f);
    static readonly Color EmotionTint = new Color(1.00f, 0.90f, 0.55f, 1f);
    static readonly Color LampTint = new Color(1.00f, 0.72f, 0.50f, 1f);

    void EnsureBgTexture()
    {
        if (_bgTex != null) return;
        _bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _bgTex.SetPixel(0, 0, Color.white);
        _bgTex.Apply();
    }

    void DrawPanelBackground()
    {
        EnsureBgTexture();
        if (_bgTex == null) return;
        Color old = GUI.color;
        GUI.color = PanelBg;
        GUI.DrawTexture(panelRect, _bgTex, ScaleMode.StretchToFill);
        GUI.color = old;
    }

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
    }

    void OnGUI()
    {
        if (!visible) return;
        if (client == null)
        {
            GUILayout.BeginArea(panelRect);
            GUILayout.Label("DogControlPanel: client 没接");
            GUILayout.EndArea();
            return;
        }

        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, backgroundAlpha);

        // 半透明底板，避免面板字和场景糊在一起看不清
        DrawPanelBackground();

        GUILayout.BeginArea(panelRect);
        _scroll = GUILayout.BeginScrollView(_scroll);

        // 标题条
        Color oldBg = GUI.backgroundColor;
        GUI.backgroundColor = HeaderBg;
        GUILayout.Box("菜鱼狗 · 数字孪生控制台  (F1 隐藏)");
        GUI.backgroundColor = oldBg;

        DrawConnection();
        DrawStatus();
        DrawActions();
        DrawDrive();
        DrawEmotions();
        DrawLamp();
        DrawMisc();

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        GUI.color = old;
    }

    // ------------------------------------------------------------------ 各分区

    /// <summary>
    /// 连接区：打包成 exe 后，别人就是靠这里填 IP 的。
    /// （Inspector 里的值会被打进构建，运行时改不了，所以必须有这个输入框。）
    /// </summary>
    void DrawConnection()
    {
        GUILayout.Label("—— 连接 ——");

        if (!_hostInit)
        {
            _hostInput = client.deviceHost;
            _hostInit = true;
        }

        GUILayout.BeginHorizontal();
        _hostInput = GUILayout.TextField(_hostInput ?? string.Empty);
        if (GUILayout.Button("连接")) ConnectTo(_hostInput);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("断开（转离线）")) GoOffline();
        if (GUILayout.Button("重连")) ConnectTo(string.IsNullOrEmpty(_hostInput) ? client.deviceHost : _hostInput);
        GUILayout.EndHorizontal();

        GUILayout.Label(client.offlineSimulation
            ? "当前：离线模拟（模拟器驱动）"
            : "当前：" + client.deviceHost + (client.Connected ? "（已连）" : "（未连）"));

        GUILayout.Space(6f);
    }

    void ConnectTo(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        client.deviceHost = host.Trim();
        client.offlineSimulation = false;
        ReconnectClient();
        Debug.Log("[菜鱼狗] 连接 " + client.deviceHost);
    }

    void GoOffline()
    {
        client.offlineSimulation = true;
        ReconnectClient();
        Debug.Log("[菜鱼狗] 切到离线模拟");
    }

    /// <summary>
    /// 按新参数重建连接。
    /// 这里直接走 Unity 的生命周期（OnDisable → OnEnable），
    /// **不依赖** DogClient 里是否有 Reconnect() 方法 ——
    /// 这样即使客户端脚本还是旧版，面板也能编译通过。
    /// </summary>
    void ReconnectClient()
    {
        if (client == null) return;
        if (!client.enabled) { client.enabled = true; return; }
        client.enabled = false;
        client.enabled = true;
    }

    void DrawStatus()
    {
        DogTelemetry t = client.Latest;
        GUILayout.Label(client.Connected ? "状态：已连接" : "状态：未连接");
        if (t != null && t.legs != null && t.legs.Length >= 4)
        {
            GUILayout.Label($"腿 {t.legs[0]},{t.legs[1]},{t.legs[2]},{t.legs[3]}");
            GUILayout.Label($"表情 {t.emotion}   动作 {t.action}");
            GUILayout.Label($"状态 {t.state}   方向 {t.drive}   忙 {t.busy}");
        }
        GUILayout.Space(4f);
    }

    void DrawActions()
    {
        GUILayout.Label("—— 动作 ——");
        Grid(ActionLabels, 3, i => client.SendAction(ActionIds[i]));
        GUILayout.Space(4f);
    }

    void DrawDrive()
    {
        GUILayout.Label("—— 摇杆（点一次保持，要停点「■」）——");
        GUILayout.BeginHorizontal();
        for (int i = 0; i < 3; ++i)
        {
            if (GUILayout.Button(DriveLabels[i])) SendDrive(DriveIds[i]);
        }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        for (int i = 3; i < DriveLabels.Length; ++i)
        {
            if (GUILayout.Button(DriveLabels[i])) SendDrive(DriveIds[i]);
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);
    }

    /// <summary>
    /// 发摇杆指令。
    ///
    /// ⚠ 停止时必须补发一个「站立」：
    /// 固件在 SetDrive(kNone) 时置 abort_ = true，而紧接着 Run() 里调用的
    /// DoStand() 会被 MoveServos 开头那句 if (aborted()) return; 拦住，
    /// 结果狗停在步态中途的姿态（腿前后张开）。
    /// 补发 action:stand 会走动作队列 → RunAction 开头清 abort_ → DoStand 才生效。
    ///
    /// 注：这是本项目对固件做「松手立停」改动后引入的副作用。
    /// 彻底修复应在固件 Run() 里、调用 DoStand() 之前清 abort_（需重新烧录）。
    /// </summary>
    void SendDrive(string dir)
    {
        client.SendDrive(dir);

        if (dir == "none")
        {
            client.SendAction("stand");
        }
    }

    void DrawEmotions()
    {
        GUILayout.Label("—— 表情 ——");
        if (GUILayout.Button("自动（跟随状态）")) client.SendEmotion("auto");

        string[] names = DogFaceTexture.EmotionNames;
        Grid(names, 3, i => client.SendEmotion(names[i]));
        GUILayout.Space(4f);
    }

    void DrawLamp()
    {
        GUILayout.Label("—— 灯带 ——");
        Grid(LampLabels, 3, i => client.SendLamp(LampIds[i]));

        GUILayout.Label($"亮度 {Mathf.RoundToInt(_brightness)}");
        float nb = GUILayout.HorizontalSlider(_brightness, 5f, 100f);
        if (Mathf.Abs(nb - _brightness) > 0.5f)
        {
            _brightness = nb;
            // 用位置参数，避免依赖参数名（旧版叫 brightness、新版叫 value）
            client.SendLamp("brightness", 255, 255, 255, Mathf.RoundToInt(_brightness));
        }

        GUILayout.Label($"灯速 {Mathf.RoundToInt(_lampSpeed)}");
        float ns = GUILayout.HorizontalSlider(_lampSpeed, 10f, 200f);
        if (Mathf.Abs(ns - _lampSpeed) > 0.5f)
        {
            _lampSpeed = ns;
            client.SendLamp("speed", 255, 255, 255, Mathf.RoundToInt(_lampSpeed));
        }
        GUILayout.Space(4f);
    }

    void DrawMisc()
    {
        GUILayout.Label("—— 其他 ——");
        GUILayout.Label($"动作速度 {Mathf.RoundToInt(_actionSpeed)}%");
        float nv = GUILayout.HorizontalSlider(_actionSpeed, 50f, 200f);
        if (Mathf.Abs(nv - _actionSpeed) > 0.5f)
        {
            _actionSpeed = nv;
            client.SendSpeed(Mathf.RoundToInt(_actionSpeed));
        }

        if (GUILayout.Button("开始 / 停止对话")) client.ToggleChat();
    }

    // ------------------------------------------------------------------ 工具

    void Grid(string[] labels, int cols, System.Action<int> onPick)
    {
        for (int i = 0; i < labels.Length; i += cols)
        {
            GUILayout.BeginHorizontal();
            int count = Mathf.Min(cols, labels.Length - i);
            for (int j = 0; j < count; ++j)
            {
                int idx = i + j;
                if (GUILayout.Button(labels[idx])) onPick(idx);
            }
            GUILayout.EndHorizontal();
        }
    }

    void Update()
    {
        // F1 显示/隐藏面板，避免挡视野
        if (Input.GetKeyDown(KeyCode.F1)) visible = !visible;
    }
}
