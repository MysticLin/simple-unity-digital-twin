// DogRigBaker.cs —— 把"你自己做的狗模型"接到孪生驱动上（Editor 工具）
//
// 菜单：Tools ▸ 菜鱼狗 ▸ 适配我自己的模型
// 也可以直接在组件上用右键菜单「① 自动适配我的模型」。
//
// 它做四件事：
//   1. 自动把你的四条腿按象限排成 左前 / 右前 / 左后 / 右后（顺序填错也能救回来）
//   2. 自动量出 legLength（髋轴 → 足底），四腿取平均
//   3. 校验常见建模错误：枢轴不在舵机轴、腿不竖直、非均匀缩放、body 父级带旋转…
//   4. 把所有引用写回 DogClient / DogBodySolver，记录静止姿态与髋点
//
// 建模约定（只要这 5 条满足，外观随便做）：
//   · 四条腿各只有 1 个枢轴（髋关节），腿是刚性段，没有膝关节
//   · 枢轴落在舵机轴上（腿的顶端），不是腿的几何中心
//   · 静止姿态（角度 90°）时腿竖直朝下
//   · 腿是 body 的后代；body 的父级不要带旋转/缩放
//   · 顺序由本工具自动排，但象限不能重复（不能两条腿都在左前）
//
// 用 SkinnedMeshRenderer 也行：把腿的顶点 100% 权重绑到对应髋骨骼即可，本工具照样能量尺寸。

using System.Text;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class DogRigBaker : MonoBehaviour
{
    [Header("你的模型")]
    [Tooltip("车体（chassis）。四条腿要是它的**子物体**，否则打开下面的自动重挂")]
    public Transform body;

    [Tooltip("四条腿的枢轴（髋关节）。顺序随便填，会自动排成 LF/RF/LB/RB")]
    public Transform[] legs = new Transform[4];

    [Header("适配选项")]
    [Tooltip("腿和 body 是兄弟节点时（Kenney 这类资源就是这样），自动把它们挂到 body 下并保持世界位置")]
    public bool reparentLegsUnderBody = true;

    [Tooltip("名字里带 left/right/front/back 时按名字定槽位（语义更准）；关掉则纯按 XZ 象限")]
    public bool preferNameOverQuadrant = true;

    [Tooltip("左右对调：接真机后发 wave / turn_left，如果虚拟狗举错腿、转向相反，把这个勾上重跑一次即可")]
    public bool swapLeftRight;

    [Header("可选")]
    [Tooltip("足端标记（贴在脚底的空物体）。不填就用腿网格包围盒的最低点")]
    public Transform[] footMarkers = new Transform[4];

    [Tooltip("四条腿长度有差异时：勾上取最大值，否则取平均")]
    public bool useMaxLegLength;

    [Header("驱动脚本（留空自动在场景里找）")]
    public DogClient client;
    public DogBodySolver solver;

    [Header("结果")]
    public bool ready;
    [TextArea(4, 24)] public string report = "把 body 和四条腿拖进来，然后右键选「① 自动适配我的模型」";

    // 车体局部 → 腿架空间（Y 上 / Z 前）的旋转；由 DetectAxes 填充
    Quaternion _toRig = Quaternion.identity;
    Vector3 _frameUp = Vector3.up;
    Vector3 _frameForward = Vector3.forward;

    static readonly string[] LegNames = { "LF", "RF", "LB", "RB" };

    [ContextMenu("① 自动适配我的模型")]
    public void AutoBind()
    {
        var log = new StringBuilder();

        // ---------- 0. 找脚本 ----------
        if (client == null) client = FindObjectOfType<DogClient>();
        if (solver == null) solver = FindObjectOfType<DogBodySolver>();

        if (client == null || solver == null)
        {
            Fail(log, "场景里找不到 DogClient / DogBodySolver，先生成骨架或手动拖进来");
            return;
        }

        if (body == null || legs == null || legs.Length < 4)
        {
            Fail(log, "body 或 legs 没填全（需要 1 个 body + 4 条腿）");
            return;
        }

        for (int i = 0; i < 4; ++i)
        {
            if (legs[i] == null)
            {
                Fail(log, $"legs[{i}] 是空的");
                return;
            }
        }

        // ---------- 0.5 腿不是 body 的子孙就重挂过去（保持世界位置） ----------
        bool reparented = false;
        for (int i = 0; i < 4; ++i)
        {
            if (IsDescendantOf(legs[i], body)) continue;
            if (!reparentLegsUnderBody)
            {
                log.AppendLine($"⚠ {legs[i].name} 不是 body 的子孙，姿态求解会算错（可打开「自动重挂」）");
                continue;
            }
            legs[i].SetParent(body, true);   // true = 保持世界位置
            reparented = true;
            log.AppendLine($"· 已把 {legs[i].name} 挂到 {body.name} 下（世界位置不变）");
        }
        if (reparented) log.AppendLine("  → solver 现在可以随 body 一起旋转四条腿了");

        // ---------- 0.9 探测车体局部坐标系的"上/前" ----------
        // Blender 直接导出的 FBX 里，body 的局部空间是 Z 向上 / -Y 向前（不是 Unity 的 Y 上 / Z 前）。
        // 不处理的话，四条腿在局部会"z 全一样"，象限排序直接冲突，适配失败。
        DetectAxes(log);

        // ---------- 1. 按象限排序（在腿架空间里：X 左右 / Z 前后） ----------
        var sorted = new Transform[4];
        for (int i = 0; i < 4; ++i)
        {
            Vector3 lp = _toRig * body.InverseTransformPoint(legs[i].position);
            int sx = lp.x < 0f ? 0 : 1;   // 0 左 1 右
            int sz = lp.z > 0f ? 0 : 1;   // 0 前 1 后
            int idx = sz * 2 + sx;        // 0 LF, 1 RF, 2 LB, 3 RB

            if (sorted[idx] != null)
            {
                Fail(log, $"象限冲突：'{legs[i].name}' 和 '{sorted[idx].name}' 都落在 {LegNames[idx]}，" +
                          "请确认四条腿分别位于车体的四个角");
                return;
            }
            sorted[idx] = legs[i];
        }

        // ---------- 1.5 名字里带 left/right/front/back 时，用名字校验/覆盖象限 ----------
        var byName = new Transform[4];
        bool nameUsable = true;
        for (int i = 0; i < 4; ++i)
        {
            int slot = SlotFromName(legs[i].name);
            if (slot < 0 || byName[slot] != null) { nameUsable = false; break; }
            byName[slot] = legs[i];
        }

        if (nameUsable)
        {
            bool conflicts = false;
            for (int i = 0; i < 4; ++i) if (byName[i] != sorted[i]) conflicts = true;

            if (conflicts)
            {
                if (preferNameOverQuadrant)
                {
                    log.AppendLine("· 名字与 XZ 象限不一致，按**名字**定槽位（语义更准）：");
                    for (int i = 0; i < 4; ++i)
                        log.AppendLine($"    {LegNames[i]} ← {byName[i].name}（象限原判 {sorted[i].name}）");
                    log.AppendLine("  说明：模型的 left/right 命名与 Unity 的 +X=右 相反，" +
                                   "多半是模型朝向 -Z。接真机后请用 turn_left 验证方向，" +
                                   "若转向相反，把左右两组对调（或改 rightMirrorSign）。");
                    sorted = byName;
                }
                else
                {
                    log.AppendLine("⚠ 名字与象限不一致，当前按象限排；若转向反了请打开「名字优先」");
                }
            }
        }

        for (int i = 0; i < 4; ++i)
        {
            if (sorted[i] != legs[i])
            {
                log.AppendLine($"· 顺序已重排：{LegNames[i]} ← {sorted[i].name}");
            }
        }

        // ---------- 1.9 左右对调（接真机实测后再决定，不用重建模型） ----------
        if (swapLeftRight)
        {
            (sorted[0], sorted[1]) = (sorted[1], sorted[0]);   // LF <-> RF
            (sorted[2], sorted[3]) = (sorted[3], sorted[2]);   // LB <-> RB
            log.AppendLine("· 已左右对调（swapLeftRight）");
        }

        // ---------- 2. 量腿长 + 几何校验 ----------
        int warnCount = 0;
        float sumLen = 0f;
        float maxLen = 0f;
        float minLen = float.MaxValue;

        for (int i = 0; i < 4; ++i)
        {
            Transform leg = sorted[i];
            Vector3 hipLocal = _toRig * body.InverseTransformPoint(leg.position);

            if (!IsDescendantOf(leg, body))
            {
                log.AppendLine($"⚠ {LegNames[i]}（{leg.name}）不是 body 的后代，姿态求解会算错");
                warnCount++;
            }

            bool hasMarker = footMarkers != null && i < footMarkers.Length && footMarkers[i] != null;
            if (!TryMeasure(leg, body, hasMarker ? footMarkers[i] : null,
                            hipLocal, out float len, out float downDeviationDeg, out bool pivotAtTop))
            {
                // 把可查线索一次列全，别只丢一句"量不出尺寸"
                int rendererCount = 0;
                float maxExtent = 0f;
                foreach (Renderer rr in leg.GetComponentsInChildren<Renderer>())
                {
                    if (rr == null) continue;
                    rendererCount++;
                    maxExtent = Mathf.Max(maxExtent, rr.bounds.size.sqrMagnitude);
                }
                log.AppendLine($"⚠ {LegNames[i]}（{leg.name}）量不出尺寸：" +
                               $"子级 Renderer {rendererCount} 个，最大包围盒 size²={maxExtent:F4}，" +
                               $"hip(腿架空间)=({hipLocal.x:F3},{hipLocal.y:F3},{hipLocal.z:F3})");
                warnCount++;
                continue;
            }

            sumLen += len;
            maxLen = Mathf.Max(maxLen, len);
            minLen = Mathf.Min(minLen, len);

            if (downDeviationDeg > 8f)
            {
                log.AppendLine($"⚠ {LegNames[i]} 静止姿态偏离竖直 {downDeviationDeg:F1}°：" +
                               "请把腿摆成竖直朝下（90°）再适配，否则整套角度都会有偏移");
                warnCount++;
            }

            if (!pivotAtTop)
            {
                log.AppendLine($"⚠ {LegNames[i]} 的枢轴看起来不在腿的顶端（舵机轴），" +
                               "旋转时腿会甩出去；请把枢轴移到髋关节处");
                warnCount++;
            }

            CheckUniformScale(leg, LegNames[i], log, ref warnCount);
        }

        CheckUniformScale(body, "body", log, ref warnCount);

        if (body.parent != null)
        {
            Vector3 ps = body.parent.lossyScale;
            float pmax = Mathf.Max(ps.x, Mathf.Max(ps.y, ps.z));
            float pmin = Mathf.Min(ps.x, Mathf.Min(ps.y, ps.z));
            bool parentNonUniform = pmax - pmin > Mathf.Max(1e-4f, pmax * 0.01f);

            if (parentNonUniform)
            {
                log.AppendLine($"⚠ body 的父级是非均匀缩放 {ps}：姿态求解会算错，" +
                               "请用统一缩放，或把缩放烘进网格");
                warnCount++;
            }
            else if (body.parent.rotation != Quaternion.identity || ps != Vector3.one)
            {
                // FBX 的轴向转换就长这样，solver 写的是世界旋转/世界高度，已兼容
                log.AppendLine($"· body 的父级带旋转/统一缩放 {ps}（FBX 轴向转换的常见形态）：已兼容，无需处理");
            }
        }

        if (maxLen > minLen * 1.1f && minLen > 0f)
        {
            string pick = useMaxLegLength ? "取最大值" : "取平均值";
            log.AppendLine($"· 四腿长度不一致（{minLen:F3} ~ {maxLen:F3}），{pick}");
        }

        float legLength = useMaxLegLength ? maxLen : (sumLen / 4f);
        if (legLength <= 0f)
        {
            log.AppendLine("");
            log.AppendLine("❌ 四条腿都没量出长度。逐条诊断（腿架空间，Y=上）：");
            for (int i = 0; i < 4; ++i)
            {
                Transform leg = sorted[i] != null ? sorted[i] : legs[i];
                Vector3 hp = _toRig * body.InverseTransformPoint(leg.position);
                bool got = TryBoundsInBody(leg, body, out Bounds bb);
                log.AppendLine(got
                    ? $"   {LegNames[i]} {leg.name}: hipY={hp.y:F3}  包围盒 Y[{bb.min.y:F3}, {bb.max.y:F3}]  腿长应为 {hp.y - bb.min.y:F3}"
                    : $"   {LegNames[i]} {leg.name}: hipY={hp.y:F3}  但腿下没有任何带网格的 Renderer");
            }
            log.AppendLine("   常见原因：填进 legs[] 的不是髋关节物体、腿网格挂在别的物体上，或模型没导入网格。");
            Fail(log, "算出的 legLength 是 0（诊断见上）");
            return;
        }

        // ---------- 3. 写回引用 ----------
        client.legRoots = sorted;
        solver.client = client;
        solver.body = body;
        solver.hips = sorted;
        solver.legLength = legLength;

        // 静止姿态 + 髋点必须在赋值之后记录
        client.CaptureRestPose();
        solver.CaptureHips();

        // ---------- 4. 报告 ----------
        log.AppendLine();
        log.AppendLine($"✅ 已适配：legLength = {legLength:F3}（单位同场景）");
        log.AppendLine("   接下来在 Play 里按 1/2/3（站/坐/睡）核对姿态；" +
                       "若前后走反了就改 DogBodySolver.swingSign = -1");

        ready = warnCount == 0;
        if (!ready) log.AppendLine($"   共 {warnCount} 条警告，建议先处理再接真机");

        report = log.ToString().Trim();
        Debug.Log("[菜鱼狗] 模型适配\n" + report);

#if UNITY_EDITOR
        EditorUtility.SetDirty(client);
        EditorUtility.SetDirty(solver);
        EditorUtility.SetDirty(this);
#endif
    }

    // ------------------------------------------------------------------ 坐标轴探测

    /// <summary>
    /// 探测车体局部空间的"上/前"，并构造 body局部 → 腿架空间 的旋转。
    ///
    /// 为什么需要：Blender 直接导出的 FBX，body 的局部空间是 **Z 上 / -Y 前**，
    /// 四条腿在局部里表现为 "z 全一样、x/y 不同"。若按 Unity 的 Y上/Z前 去判象限，
    /// 四条腿会全部落在同一侧 → 报"象限冲突"，适配直接失败。
    ///
    /// 判据：
    ///   ① 四个髋点里**跨度最小**的轴 = 高度轴（四条腿等高）
    ///   ② 高度轴的正负：腿的网格在枢轴**下方**，所以取反
    ///   ③ 剩下两轴里**跨度大**的是前后轴（狗的轴距 > 轮距）
    ///   ④ 前后正负：名字里带 front 的腿应落在 +forward
    /// </summary>
    void DetectAxes(StringBuilder log)
    {
        var hipLocal = new Vector3[4];
        for (int i = 0; i < 4; ++i) hipLocal[i] = body.InverseTransformPoint(legs[i].position);

        var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < 4; ++i)
        {
            for (int a = 0; a < 3; ++a)
            {
                float v = Comp(hipLocal[i], a);
                if (v < mn[a]) mn[a] = v;
                if (v > mx[a]) mx[a] = v;
            }
        }
        var spread = new[] { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };

        // ① 高度轴 = 跨度最小
        int upAxis = 0;
        if (spread[1] < spread[upAxis]) upAxis = 1;
        if (spread[2] < spread[upAxis]) upAxis = 2;

        // ② 高度正负：腿网格在枢轴下方
        Quaternion saved = _toRig;
        _toRig = Quaternion.identity;
        Vector3 downAccum = Vector3.zero;
        for (int i = 0; i < 4; ++i)
        {
            if (!TryBoundsInBody(legs[i], body, out Bounds b)) continue;
            downAccum += (b.center - hipLocal[i]);
        }
        _toRig = saved;
        float upSign = Comp(downAccum, upAxis) > 0f ? -1f : 1f;
        Vector3 up = UnitAxis(upAxis) * upSign;

        // ③ 前后轴 = 剩下两轴里跨度大的
        int fwdAxis = -1;
        for (int a = 0; a < 3; ++a)
        {
            if (a == upAxis) continue;
            if (fwdAxis < 0 || spread[a] > spread[fwdAxis]) fwdAxis = a;
        }

        // ④ 前后正负：按名字
        float frontSum = 0f;
        int frontCount = 0;
        for (int i = 0; i < 4; ++i)
        {
            string n = legs[i].name.ToLowerInvariant();
            if (n.Contains("front") || n.Contains("fore") || n.Contains("fwd"))
            {
                frontSum += Comp(hipLocal[i], fwdAxis);
                frontCount++;
            }
        }
        float fwdSign = (frontCount > 0 && frontSum < 0f) ? -1f : 1f;
        Vector3 fwd = UnitAxis(fwdAxis) * fwdSign;

        _frameUp = up;
        _frameForward = fwd;
        // ⚠ 必须取逆：LookRotation(fwd, up) 是「腿架空间 → body 局部」的旋转
        // （它把 +Z 映射到 fwd、+Y 映射到 up）。我们要的是 body 局部 → 腿架空间，
        // 所以是它的逆。用错方向会让腿的"上"变成镜像，量出的腿长正好是 0。
        _toRig = Quaternion.Inverse(Quaternion.LookRotation(fwd, up));

        // 写回 solver：正运动学必须用同一套轴
        if (solver != null)
        {
            solver.bodyUp = up;
            solver.bodyForward = fwd;
        }

        string conv = (upAxis == 1 && fwdAxis == 2) ? "Unity 系（Y 上 / Z 前）"
                    : (upAxis == 2 && fwdAxis == 1) ? "Blender 系（Z 上 / -Y 前）→ 已自动换算"
                    : "非常规轴序 → 已按数据换算";
        log.AppendLine($"· 坐标约定：{conv}");
        log.AppendLine($"  body局部 up={Fmt(up)}  forward={Fmt(fwd)}   " +
                       $"跨度 X{spread[0]:F2} Y{spread[1]:F2} Z{spread[2]:F2}");
    }

    static float Comp(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

    static Vector3 UnitAxis(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

    static string Fmt(Vector3 v) => $"({v.x:F0},{v.y:F0},{v.z:F0})";

    // ------------------------------------------------------------------ 度量

    bool TryMeasure(Transform leg, Transform bodyT, Transform footMarker, Vector3 hipLocal,
                    out float length, out float downDeviationDeg, out bool pivotAtTop)
    {
        length = 0f;
        downDeviationDeg = 0f;
        pivotAtTop = true;

        Vector3 footLocal;
        if (footMarker != null)
        {
            footLocal = _toRig * bodyT.InverseTransformPoint(footMarker.position);
        }
        else
        {
            if (!TryBoundsInBody(leg, bodyT, out Bounds b)) return false;
            footLocal = new Vector3(hipLocal.x, b.min.y, hipLocal.z);

            // 枢轴应该在包围盒顶部附近（不超过总长的 20%）
            float topGap = b.max.y - hipLocal.y;
            float total = Mathf.Max(1e-4f, hipLocal.y - b.min.y);
            pivotAtTop = topGap < total * 0.2f;
        }

        Vector3 dir = footLocal - hipLocal;
        length = dir.magnitude;
        if (length < 1e-4f) return false;

        downDeviationDeg = Vector3.Angle(dir.normalized, Vector3.down);
        return true;
    }

    /// <summary>把腿（含子物体，比如轮子）的世界包围盒换到 body 局部、再转到腿架空间。</summary>
    bool TryBoundsInBody(Transform leg, Transform bodyT, out Bounds result)
    {
        result = new Bounds();
        bool any = false;

        foreach (Renderer r in leg.GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled) continue;
            Bounds wb = r.bounds;
            if (wb.size.sqrMagnitude <= 1e-8f) continue;

            Vector3 e = wb.extents;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 corner = wb.center + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = _toRig * bodyT.InverseTransformPoint(corner);

                if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                else result.Encapsulate(p);
            }
        }

        return any;
    }

    static void CheckUniformScale(Transform t, string label, StringBuilder log, ref int warnCount)
    {
        Vector3 s = t.lossyScale;
        float max = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
        float min = Mathf.Min(s.x, Mathf.Min(s.y, s.z));
        if (max - min > Mathf.Max(1e-3f, max * 0.01f))
        {
            log.AppendLine($"⚠ {label}（{t.name}）是非均匀缩放 {s}，子物体的旋转轴会被拉歪；" +
                           "请用 uniform scale，或把缩放烘到网格里");
            warnCount++;
        }
    }

    /// <summary>从名字里猜槽位：0=LF 1=RF 2=LB 3=RB；猜不出返回 -1。</summary>
    static int SlotFromName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return -1;
        string s = rawName.ToLowerInvariant();

        bool front = s.Contains("front") || s.Contains("fore") || s.Contains("fwd");
        bool back = s.Contains("back") || s.Contains("rear") || s.Contains("hind");
        bool left = s.Contains("left");
        bool right = s.Contains("right");

        if (!(front ^ back) || !(left ^ right)) return -1;
        return (front ? 0 : 2) + (left ? 0 : 1);
    }

    static bool IsDescendantOf(Transform t, Transform root)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            if (p == root) return true;
        }
        return false;
    }

    void Fail(StringBuilder log, string message)
    {
        ready = false;
        report = "❌ " + message;
        log.AppendLine(report);
        Debug.LogError("[菜鱼狗] " + message);
    }
}

#if UNITY_EDITOR
static class DogRigBakerMenu
{
    [MenuItem("Tools/菜鱼狗/适配我自己的模型")]
    static void OpenBaker()
    {
        var baker = Object.FindObjectOfType<DogRigBaker>();
        if (baker == null)
        {
            var go = new GameObject("DogRigBaker");
            baker = go.AddComponent<DogRigBaker>();
            Undo.RegisterCreatedObjectUndo(go, "Add DogRigBaker");
        }

        baker.AutoBind();
        Selection.activeGameObject = baker.gameObject;
    }
}
#endif
