// DogFaceTexture.cs —— 把真狗脸上那块 128×64 的 SH1106 OLED 原样搬进 Unity
//
// 真狗的"脸"不是模型，是一块单色小屏：两颗圆角几何眼睛 + 睡觉时的 Zzz。
// 本脚本 1:1 复刻固件 main/boards/caiyu-c3/caiyu_eye_face.cc 的几何参数
// （EyeConfig：offset / height / width / slope_top / slope_bottom / radius_top / radius_bottom），
// 把 21 个表情渲染成一张 128×64 的贴图，直接贴到屏幕 Quad 的材质上。
//
// 用法：
//   1. 场景里做一个 128:64 比例的 Quad 当"屏幕"（贴在小狗前脸）
//   2. 材质随便一个 Unlit（建议自发光），把这个脚本挂到任意物体上
//   3. 把 Quad 的 Renderer 拖到 screenRenderer，把 DogClient 拖到 client
//   4. 运行后 emotion 字段一变，屏幕就跟着变；睡觉时会自动呼吸 + 飘 Zzz

using System.Collections.Generic;
using UnityEngine;

public class DogFaceTexture : MonoBehaviour
{
    public const int Width = 128;
    public const int Height = 64;

    // 与固件一致：眼距 8px、眼球基准 40px、屏幕中心 (64, 32)
    const int CenterX = 64;
    const int CenterY = 32;
    const int EyeSize = 40;
    const int EyeInterDistance = 8;

    [Header("引用")]
    public DogClient client;
    [Tooltip("模型里的屏幕部件（名字一般叫 screen / face）。必须是有 UV 的平面或 Quad，否则贴图会糊")]
    public Renderer screenRenderer;

    [Header("贴图方向校正")]
    [Tooltip("表情左右镜像就勾这个（不用改模型）")]
    public bool flipHorizontal;
    [Tooltip("表情上下颠倒就勾这个")]
    public bool flipVertical;

    [Header("屏幕外观")]
    public Color32 eyeColor = new Color32(255, 255, 255, 255);
    public Color32 backgroundColor = new Color32(0, 0, 0, 255);
    [Range(0f, 4f)] public float emissionBoost = 1.2f;

    [Header("动画")]
    public bool autoBlink = true;
    public bool animateSleep = true;
    [Range(10, 60)] public int fps = 30;

    [Header("演示（不需要连设备）")]
    [Tooltip("勾上后自动轮播全部 21 种表情，Console 会打印当前名字")]
    public bool demoCycle;
    [Tooltip("轮播间隔（秒）")]
    public float demoIntervalSec = 1.5f;

    /// <summary>当前渲染出来的贴图，可给自定义 shader 用。</summary>
    public Texture2D Texture => _tex;

    /// <summary>当前表情名（设备侧的 21 个名字之一）。</summary>
    public string Emotion => _emotion;

    struct EyeCfg
    {
        public float ox, oy, h, w, slTop, slBot, rt, rb;

        public EyeCfg(float ox, float oy, float h, float w, float slTop, float slBot, float rt, float rb)
        {
            this.ox = ox; this.oy = oy; this.h = h; this.w = w;
            this.slTop = slTop; this.slBot = slBot; this.rt = rt; this.rb = rb;
        }
    }

    // 固件 caiyu_eye_face.cc 里的预设值（顺序：ox, oy, h, w, slopeTop, slopeBot, radiusTop, radiusBottom）
    static readonly Dictionary<string, EyeCfg> Presets = new Dictionary<string, EyeCfg>
    {
        { "neutral",     new EyeCfg( 0f, 0f, 40f, 32f, 0.00f, 0.00f, 10f, 10f) },
        { "happy",       new EyeCfg( 0f, 1f, 26f, 34f, 0.00f, 0.00f, 19f, 6f) },
        { "laughing",    new EyeCfg( 0f, 2f, 28f, 34f, 0.00f, 0.00f, 22f, 9f) },
        { "funny",       new EyeCfg( 0f, 0f, 7f, 28f, 0.00f, 0.00f, 3f, 3f) },
        { "sad",         new EyeCfg( 0f, 2f, 22f, 30f, 0.00f, 0.00f, 11f, 11f) },
        { "angry",       new EyeCfg( -2f, 0f, 22f, 30f, 0.60f, 0.00f, 1f, 14f) },
        { "crying",      new EyeCfg( 0f, 0f, 14f, 24f, -0.35f, 0.00f, 2f, 10f) },
        { "loving",      new EyeCfg( 0f, 0f, 36f, 34f, 0.00f, 0.00f, 0f, 0f) }, // 走心形
        { "embarrassed", new EyeCfg( 0f, 4f, 18f, 30f, 0.00f, 0.00f, 9f, 11f) },
        { "surprised",   new EyeCfg( -1f, 0f, 44f, 38f, 0.00f, 0.00f, 18f, 18f) },
        { "shocked",     new EyeCfg( 0f, 0f, 24f, 24f, 0.00f, 0.00f, 12f, 12f) },
        { "thinking",    new EyeCfg( 0f, -4f, 16f, 28f, 0.50f, 0.00f, 0f, 8f) },
        { "winking",     new EyeCfg( 0f, 0f, 14f, 26f, 0.00f, 0.00f, 13f, 0f) },
        { "cool",        new EyeCfg( 2f, -1f, 14f, 34f, 0.00f, 0.20f, 1f, 5f) },
        { "relaxed",     new EyeCfg( 0f, 0f, 22f, 32f, 0.00f, 0.00f, 11f, 11f) },
        { "delicious",   new EyeCfg( 0f, 0f, 9f, 34f, 0.00f, 0.00f, 15f, 15f) },
        { "kissy",       new EyeCfg( 0f, -7f, 16f, 16f, 0.00f, 0.00f, 8f, 8f) },
        { "confident",   new EyeCfg( 0f, 0f, 20f, 26f, 0.30f, 0.00f, 2f, 10f) },
        { "sleepy",      new EyeCfg( 0f, -1f, 7f, 32f, -0.25f, -0.25f, 3f, 3f) },
        { "silly",       new EyeCfg( 0f, 0f, 42f, 34f, 0.00f, 0.00f, 13f, 13f) },
        { "confused",    new EyeCfg( 0f, 0f, 28f, 26f, -0.15f, 0.00f, 4f, 13f) },
    };

    // 睡觉 Zzz 的三段（与固件 kSleepMarks 一致）
    static readonly Vector3Int[] SleepMarks =
    {
        new Vector3Int(88, 19, 7),
        new Vector3Int(99, 11, 9),
        new Vector3Int(110, 3, 11),
    };
    const float BreathPeriodMs = 3000f;
    const float BlinkIntervalMs = 3500f;
    const float BlinkDurationMs = 180f;

    /// <summary>固件 caiyu_emotion.cc 里的 21 个表情名（顺序一致）</summary>
    public static readonly string[] EmotionNames =
    {
        "neutral", "happy", "laughing", "funny", "sad", "angry", "crying", "loving",
        "embarrassed", "surprised", "shocked", "thinking", "winking", "cool", "relaxed",
        "delicious", "kissy", "confident", "sleepy", "silly", "confused",
    };

    float _demoTimer;
    int _demoIndex = -1;

    Texture2D _tex;
    Color32[] _buf;
    string _emotion = "neutral";
    float _frameAccum;
    float _blinkTimer;
    float _blinkPhase = -1f;

    void Awake()
    {
        _buf = new Color32[Width * Height];
        _tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "CaiyuFace",
        };
        _blinkTimer = BlinkIntervalMs;
        ApplyToMaterial();
        Redraw();
    }

    void OnEnable()
    {
        if (client != null) client.EmotionChanged += SetEmotion;
    }

    void OnDisable()
    {
        if (client != null) client.EmotionChanged -= SetEmotion;
    }

    /// <summary>切换表情（传设备侧的 21 个名字之一；不认识的按 neutral 处理）。</summary>
    public void SetEmotion(string emotion)
    {
        if (string.IsNullOrEmpty(emotion)) emotion = "neutral";
        if (_emotion == emotion) return;
        _emotion = emotion;
        Redraw();
    }

    [ContextMenu("下一个表情")]
    void NextEmotionFromMenu()
    {
        _demoIndex = (_demoIndex + 1) % EmotionNames.Length;
        Debug.Log($"[菜鱼狗] 表情 {_demoIndex + 1}/{EmotionNames.Length}: {EmotionNames[_demoIndex]}");
        SetEmotion(EmotionNames[_demoIndex]);
    }

    void Update()
    {
        // 演示模式：自动轮播 21 种，不需要连任何设备
        if (demoCycle)
        {
            _demoTimer += Time.deltaTime;
            if (_demoTimer >= Mathf.Max(0.3f, demoIntervalSec))
            {
                _demoTimer = 0f;
                _demoIndex = (_demoIndex + 1) % EmotionNames.Length;
                Debug.Log($"[菜鱼狗] 表情 {_demoIndex + 1}/{EmotionNames.Length}: {EmotionNames[_demoIndex]}");
                SetEmotion(EmotionNames[_demoIndex]);
            }
        }
        else if (client != null && !string.IsNullOrEmpty(client.Latest?.emotion) &&
                 client.Latest.emotion != _emotion)
        {
            _emotion = client.Latest.emotion;
        }

        // 节流到 fps，128×64 重绘本身很便宜，但没必要每帧都做
        _frameAccum += Time.deltaTime;
        if (_frameAccum < 1f / Mathf.Max(1, fps)) return;
        _frameAccum = 0f;

        UpdateBlink();
        Redraw();
    }

    void UpdateBlink()
    {
        if (!autoBlink || _emotion == "sleepy")
        {
            _blinkPhase = -1f;
            return;
        }

        _blinkTimer -= Time.deltaTime * 1000f;
        if (_blinkTimer <= 0f)
        {
            _blinkTimer = BlinkIntervalMs;
            _blinkPhase = 0f;
        }

        if (_blinkPhase >= 0f)
        {
            _blinkPhase += Time.deltaTime * 1000f;
            if (_blinkPhase > BlinkDurationMs) _blinkPhase = -1f;
        }
    }

    // ------------------------------------------------------------------ 渲染

    void Redraw()
    {
        for (int i = 0; i < _buf.Length; ++i) _buf[i] = backgroundColor;

        if (!Presets.TryGetValue(_emotion, out EyeCfg cfg)) cfg = Presets["neutral"];

        bool sleeping = _emotion == "sleepy";
        bool heart = _emotion == "loving";

        float breath = (sleeping && animateSleep) ? BreathOffset() : 0f;

        // 眨眼：梯形包络 0→1→0，同时把高度压到 2px、宽度拉到 60px（固件的 EyeBlink）
        float blinkT = 0f;
        if (_blinkPhase >= 0f)
        {
            float p = Mathf.Clamp01(_blinkPhase / BlinkDurationMs);
            blinkT = p < 0.5f ? p * 2f : (1f - p) * 2f;
        }

        int cy = CenterY + Mathf.RoundToInt(breath);
        int leftCx = CenterX - EyeSize / 2 - EyeInterDistance;
        int rightCx = CenterX + EyeSize / 2 + EyeInterDistance;

        if (heart)
        {
            DrawHeart(leftCx, cy, cfg);
            DrawHeart(rightCx, cy, cfg);
        }
        else
        {
            DrawEye(leftCx, cy, ApplyBlink(cfg, blinkT));
            // 右眼按表情单独缩放：winking 才会真的眨一只眼
            EyeCfg rightCfg = ApplyBlink(ScaleCfg(cfg, RightEyeScale(_emotion)), blinkT);
            DrawEye(rightCx, cy, rightCfg);

            // happy/laughing：挖掉下部留 ∩；sad：挖掉上部留 ∪
            if (CarveFor(_emotion, out float carveThick, out float carveInset, out bool carveKeepTop))
            {
                CarveBand(leftCx, cy, cfg, carveThick, carveInset, carveKeepTop);
                CarveBand(rightCx, cy, rightCfg, carveThick, carveInset, carveKeepTop);
            }
        }

        DrawDeco(_emotion, leftCx, rightCx, cy);

        if (sleeping && animateSleep) DrawSleepMarks();

        _tex.SetPixels32(_buf);
        _tex.Apply(false, false);
    }

    static EyeCfg ApplyBlink(EyeCfg cfg, float t)
    {
        if (t <= 0f) return cfg;
        float h = Mathf.Lerp(cfg.h, 2f, t);
        float w = Mathf.Lerp(cfg.w, 60f, t);   // 固件 EyeBlink 的 blink_height / blink_width
        return new EyeCfg(cfg.ox, cfg.oy, h, w, cfg.slTop, cfg.slBot, cfg.rt, cfg.rb);
    }

    static float BreathOffset()
    {
        float phase = Time.time * 1000f % BreathPeriodMs;
        float half = BreathPeriodMs * 0.5f;
        const float amp = 2f;
        return phase < half ? -amp + 2f * amp * phase / half : amp - 2f * amp * (phase - half) / half;
    }

    void DrawSleepMarks()
    {
        float cycle = Time.time * 1000f % BreathPeriodMs;
        float step = BreathPeriodMs / SleepMarks.Length;
        int visible = Mathf.Clamp(Mathf.FloorToInt(cycle / step) + 1, 1, SleepMarks.Length);

        for (int i = 0; i < visible; ++i)
        {
            Vector3Int m = SleepMarks[i];
            DrawZ(m.x, m.y, m.z);
        }
    }

    /// <summary>
    /// 与固件 DrawEyeShape 等价：把 eye_config 的四个控制点四边形向外扩 radius，
    /// 拐角自然变成圆弧（凸多边形的 SDF 偏移）。
    /// </summary>
    /// <summary>
    /// 圆角矩形眼（上下圆角可不同 + 上下边倾斜）。
    ///
    /// ⚠ 旧实现用"各边半平面距离取 max 再减 r"判内外，那是 **L∞ 膨胀**：
    /// 角是尖的，于是 neutral/surprised 画成方块、一加 slope 就变成三角形楔子。
    /// 正确写法是标准圆角矩形 SDF：
    ///   q = |p| - (half - r);  d = length(max(q,0)) + min(max(qx,qy),0) - r
    ///
    /// slope 用 shear 近似成平行四边形（dx 越大，上下边整体偏移越多），
    /// 视觉上就是"垂眼/压眉"，不再是尖锐的楔形。
    /// </summary>
    void DrawEye(int cx, int cy, EyeCfg c)
    {
        float ocx = cx + c.ox;
        float ocy = cy + c.oy;
        float hw = c.w * 0.5f;
        float hh = c.h * 0.5f;
        float shear = (c.slTop + c.slBot) * 0.25f;
        float lim = Mathf.Min(hw, hh);
        float rt = Mathf.Clamp(c.rt, 0f, lim);
        float rb = Mathf.Clamp(c.rb, 0f, lim);

        int bbox = Mathf.CeilToInt(Mathf.Max(hw, hh) + 2f);
        int x0 = Mathf.Max(0, Mathf.FloorToInt(ocx) - bbox);
        int x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(ocx) + bbox);
        int y0 = Mathf.Max(0, Mathf.FloorToInt(ocy) - bbox);
        int y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(ocy) + bbox);

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                float dx = x + 0.5f - ocx;
                float dy = y + 0.5f - ocy - shear * dx;
                float r = dy < 0f ? rt : rb;
                float qx = Mathf.Abs(dx) - (hw - r);
                float qy = Mathf.Abs(dy) - (hh - r);
                float ox = Mathf.Max(qx, 0f);
                float oy = Mathf.Max(qy, 0f);
                float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                if (d <= 0f) SetPixel(x, y, eyeColor);
            }
        }
    }

    /// <summary>
    /// 弯月带：保留顶部（keepTop=true）或底部 thick 像素，其余整块挖掉。
    ///
    /// 两个坑：
    ///  ① 用**椭圆**挖会把眼顶的弧也切掉，笑眼变成两个钩子 —— 所以用"带"而不是椭圆；
    ///  ② inset 必须为**负**（挖得比眼睛还宽），否则两侧各留一条竖边，看着像门框。
    /// </summary>
    void CarveBand(int cx, int cy, EyeCfg c, float thick, float inset, bool keepTop)
    {
        float ocx = cx + c.ox;
        float ocy = cy + c.oy;
        float hh = c.h * 0.5f;
        float hw = c.w * 0.5f - inset;
        float cyc = keepTop ? ocy + thick * 0.5f : ocy - thick * 0.5f;
        float chh = Mathf.Max(0.5f, hh - thick * 0.5f);
        float r = Mathf.Max(0f, Mathf.Min(Mathf.Min(c.rt, c.rb), Mathf.Min(hw, chh)) - thick * 0.45f);

        int x0 = Mathf.Max(0, Mathf.FloorToInt(ocx - hw) - 1);
        int x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(ocx + hw) + 1);
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cyc - chh) - 1);
        int y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(cyc + chh) + 1);

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                float dx = x + 0.5f - ocx;
                float dy = y + 0.5f - cyc;
                float qx = Mathf.Abs(dx) - (hw - r);
                float qy = Mathf.Abs(dy) - (chh - r);
                float ox = Mathf.Max(qx, 0f);
                float oy = Mathf.Max(qy, 0f);
                float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                if (d <= 0f) SetPixel(x, y, backgroundColor);
            }
        }
    }

    /// <summary>需要弯月的表情：带厚 thick、横向内缩 inset（负数=外扩）、保留哪一边</summary>
    static bool CarveFor(string e, out float thick, out float inset, out bool keepTop)
    {
        thick = 0f;
        inset = 0f;
        keepTop = true;
        if (e == "happy") { thick = 8f; inset = -6f; keepTop = true; return true; }
        if (e == "laughing") { thick = 10f; inset = -7f; keepTop = true; return true; }
        if (e == "sad") { thick = 9f; inset = -5f; keepTop = false; return true; }
        return false;
    }

    void DrawHeart(int cx, int cy, EyeCfg c)
    {
        float w = c.w;
        float h = c.h;
        float ccx = cx + c.ox;
        float ccy = cy + c.oy;

        if (h < 6f)
        {
            DrawHLine(Mathf.RoundToInt(ccx - w * 0.5f), Mathf.RoundToInt(ccy),
                      Mathf.RoundToInt(w), eyeColor);
            return;
        }

        float r = w * 0.25f;
        float lobeY = ccy - h * 0.25f;

        FillEllipse(ccx - r, lobeY, r, r, eyeColor);
        FillEllipse(ccx + r, lobeY, r, r, eyeColor);
        FillTriangle(new Vector2(ccx - w * 0.5f, lobeY),
                     new Vector2(ccx + w * 0.5f, lobeY),
                     new Vector2(ccx, ccy + h * 0.5f), eyeColor);
    }

    void DrawZ(int x, int y, int size)
    {
        if (size < 3) return;
        DrawHLine(x, y, size, eyeColor);
        DrawHLine(x, y + size - 1, size, eyeColor);
        DrawLine(x + size - 1, y + 1, x, y + size - 2, eyeColor);
    }

    // ------------------------------------------------------------------ 像素原语

    void SetPixel(int x, int y, Color32 c)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return;
        _buf[(Height - 1 - y) * Width + x] = c;   // Unity 行 0 在底部，这里做上下翻转
    }

    void DrawHLine(int x, int y, int len, Color32 c)
    {
        if (len <= 0 || y < 0 || y >= Height) return;
        int from = Mathf.Max(x, 0);
        int to = Mathf.Min(x + len, Width);
        for (int i = from; i < to; ++i) SetPixel(i, y, c);
    }

    void DrawLine(int x0, int y0, int x1, int y1, Color32 c)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetPixel(x0, y0, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    void FillEllipse(float cx, float cy, float rx, float ry, Color32 c)
    {
        if (rx <= 0f || ry <= 0f) return;
        int x0 = Mathf.FloorToInt(cx - rx);
        int x1 = Mathf.CeilToInt(cx + rx);
        int y0 = Mathf.FloorToInt(cy - ry);
        int y1 = Mathf.CeilToInt(cy + ry);

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                float nx = (x + 0.5f - cx) / rx;
                float ny = (y + 0.5f - cy) / ry;
                if (nx * nx + ny * ny <= 1f) SetPixel(x, y, c);
            }
        }
    }

    void FillTriangle(Vector2 a, Vector2 b, Vector2 d, Color32 c)
    {
        int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, d.x)));
        int x1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, d.x)));
        int y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, d.y)));
        int y1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, d.y)));

        float area = (b.x - a.x) * (d.y - a.y) - (d.x - a.x) * (b.y - a.y);
        if (Mathf.Abs(area) < 1e-6f) return;
        float invArea = 1f / area;

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = ((b.x - p.x) * (d.y - p.y) - (d.x - p.x) * (b.y - p.y)) * invArea;
                float w1 = ((d.x - p.x) * (a.y - p.y) - (a.x - p.x) * (d.y - p.y)) * invArea;
                float w2 = 1f - w0 - w1;
                if (w0 >= 0f && w1 >= 0f && w2 >= 0f) SetPixel(x, y, c);
            }
        }
    }

    // ------------------------------------------------------------------ 不对称 & 装饰

    /// <summary>
    /// 右眼造型缩放。有些表情必须左右不对称才认得出来（比如 winking 是真的眨一只眼），
    /// 而 Presets 里一对眼睛是同一个 EyeConfig，所以这里单独给右眼乘一个系数。
    /// </summary>
    static Vector2 RightEyeScale(string e)
    {
        if (e == "winking") return new Vector2(0.28f, 1.00f);   // 右眼闭成一条线
        if (e == "silly") return new Vector2(0.50f, 1.00f);
        if (e == "kissy") return new Vector2(0.55f, 0.90f);
        if (e == "thinking") return new Vector2(1.30f, 1.10f);
        if (e == "confused") return new Vector2(1.35f, 1.05f);
        return new Vector2(1f, 1f);
    }

    static EyeCfg ScaleCfg(EyeCfg c, Vector2 k)
    {
        return new EyeCfg(c.ox, c.oy, c.h * k.x, c.w * k.y, c.slTop, c.slBot, c.rt, c.rb);
    }

    /// <summary>装饰：让形状相近的表情也能一眼区分（不改变 21 种的名字/协议）</summary>
    void DrawDeco(string e, int leftCx, int rightCx, int cy)
    {
        if (e == "crying") DrawTear(rightCx + 12, cy + 12);
        else if (e == "thinking" || e == "confused") DrawQuestion(rightCx + 6, cy - 20);
        else if (e == "cool") DrawSunglass(leftCx, rightCx, cy);
        else if (e == "angry") DrawAngryBrows(leftCx, rightCx, cy);
    }

    /// <summary>怒眉：左右镜像，向内侧下压，中间呈 V 字</summary>
    void DrawAngryBrows(int leftCx, int rightCx, int cy)
    {
        for (int k = 0; k < 2; ++k)
        {
            int cx = k == 0 ? leftCx : rightCx;
            int dir = k == 0 ? 1 : -1;
            for (int i = 0; i < 15; ++i)
            {
                int x = cx - 8 * dir + i * dir;
                int y = cy - 20 + Mathf.RoundToInt(i / 14f * 5f);
                SetPixel(x, y, eyeColor);
                SetPixel(x, y + 1, eyeColor);
                SetPixel(x, y + 2, eyeColor);
            }
        }
    }

    void DrawTear(int x, int y)
    {
        FillEllipse(x, y + 4f, 3.0f, 4.0f, eyeColor);
        SetPixel(x, y - 1, eyeColor);
        SetPixel(x, y, eyeColor);
        SetPixel(x, y + 1, eyeColor);
        SetPixel(x - 1, y, eyeColor);
        SetPixel(x + 1, y, eyeColor);
    }

    void DrawQuestion(int x, int y)
    {
        DrawHLine(x - 4, y, 9, eyeColor);
        DrawHLine(x - 3, y + 9, 6, eyeColor);
        SetPixel(x + 5, y + 1, eyeColor);
        SetPixel(x + 5, y + 2, eyeColor);
        SetPixel(x + 5, y + 3, eyeColor);
        SetPixel(x + 3, y + 4, eyeColor);
        SetPixel(x + 3, y + 5, eyeColor);
        SetPixel(x + 2, y + 6, eyeColor);
        SetPixel(x + 3, y + 8, eyeColor);
        SetPixel(x + 4, y + 8, eyeColor);
    }

    /// <summary>墨镜：3px 粗的横框，贯穿两眼并向外伸出，读作"戴墨镜"</summary>
    void DrawSunglass(int leftCx, int rightCx, int cy)
    {
        int x0 = leftCx - 20;
        int x1 = rightCx + 20;
        int len = x1 - x0 + 1;
        for (int t = 0; t < 3; ++t) DrawHLine(x0, cy - 6 + t, len, eyeColor);
    }

    // ------------------------------------------------------------------ 材质

    void ApplyToMaterial()
    {
        if (screenRenderer == null)
        {
            Debug.LogWarning("[菜鱼狗] DogFaceTexture.screenRenderer 没接：表情不会显示。" +
                             "把模型里的 screen/face 部件拖进来即可。");
            return;
        }

        Material mat = screenRenderer.material;   // 会克隆一份实例，不动共享材质

        // 渲染器上没材质时自己建一个 Unlit，避免表情贴图无处可贴
        if (mat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Texture");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null)
            {
                mat = new Material(sh) { name = "CaiyuFaceRuntime" };
                screenRenderer.material = mat;
            }
        }
        if (mat == null) return;

        Color col = eyeColor;
        col.r *= emissionBoost;
        col.g *= emissionBoost;
        col.b *= emissionBoost;

        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", _tex);      // URP
            ApplyTiling(mat, "_BaseMap");
        }
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTexture("_MainTex", _tex);      // Built-in
            ApplyTiling(mat, "_MainTex");
        }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", col);
    }

    /// <summary>贴图方向校正：不改模型，靠 UV 缩放/偏移把镜像翻回来。</summary>
    void ApplyTiling(Material mat, string prop)
    {
        float sx = flipHorizontal ? -1f : 1f;
        float sy = flipVertical ? -1f : 1f;
        mat.SetTextureScale(prop, new Vector2(sx, sy));
        mat.SetTextureOffset(prop, new Vector2(flipHorizontal ? 1f : 0f, flipVertical ? 1f : 0f));
    }
}
