// DogBodySolver.cs —— 用四条腿的角度反推车体姿态（真孪生的关键一块）
//
// v2 修正（重要）：FitNormal() 原来用的是 0→1→2→3 的"蝴蝶序"切三角形，
// 两条三角形法线会互相抵消，真实动作下残出水平法线，把车体翻 90°，
// 表现为"脚本来在地面上、一动起来腿组跑到竖直平面里"。现已改为按地面绕一圈
// 的顺序做 Newell 积分。详见 FitNormal() 上方的注释。
//
// 菜鱼狗没有 IMU、也没有车身舵机：车体的高度/俯仰/横滚完全由 4 条腿的摆角决定。
// 本脚本做两件事：
//   1) 正运动学：髋点 + 腿长 × 腿方向  →  四个足端坐标（车身空间）
//   2) 平面拟合：让四足所在平面保持水平 →  反解车体的 world 旋转与高度
//
// 这样 stand / sit / sleep / walk 全都会自动"看起来对"：
//   stand 四腿 90° → 腿竖直 → 车体抬到最高
//   sleep 前腿 180°、后腿 0° → 腿水平摊开 → 车体自然落地
//
// 前置约定（很重要）：
//   · hips[] 必须是 body 的**后代**（腿也挂在 body 下面），本脚本按车身空间算足端
//   · 腿在静止姿态（角度 90°）应当**竖直朝下**
//   · body 的父级不要带旋转/缩放，脚本只写 body.localRotation 与 localPosition.y
//   · 与 DogClient 的 bodyTiltGain 二选一：用本脚本就把 client.body 留空

using UnityEngine;

public class DogBodySolver : MonoBehaviour
{
    [Header("引用")]
    public DogClient client;
    [Tooltip("车体（chassis）。腿必须是它的后代")]
    public Transform body;
    [Tooltip("4 个髋关节锚点，顺序 左前、右前、左后、右后")]
    public Transform[] hips = new Transform[4];

    [Header("几何（用尺子量真狗）")]
    [Tooltip("髋轴到足底接触点的长度，单位与场景一致")]
    public float legLength = 2.6f;
    [Tooltip("摆动方向。若发现 walk 走成了 walk_back（前后颠倒），改成 -1")]
    public float swingSign = 1f;

    [Header("坐标轴（RigBaker 会自动探测并填入）")]
    [Tooltip("车体局部空间里的\"上\"。默认 Unity 系 (0,1,0)；Blender 直接导出的模型是 (0,0,1)")]
    public Vector3 bodyUp = Vector3.up;
    [Tooltip("车体局部空间里的\"前\"。默认 Unity 系 (0,0,1)；Blender 系是 (0,-1,0)")]
    public Vector3 bodyForward = Vector3.forward;

    [Header("表现")]
    [Tooltip("车体姿态平滑系数，越小越柔")]
    [Range(0.02f, 1f)] public float smoothing = 0.25f;
    [Tooltip("足底所在平面高度（世界 Y）")]
    public float groundY = 0f;
    [Tooltip("关掉则只解姿态、不贴地")]
    public bool snapToGround = true;

    [Header("调试")]
    public bool drawGizmos = true;

    readonly Vector3[] _hipLocal = new Vector3[4];
    readonly Vector3[] _footLocal = new Vector3[4];
    bool _captured;
    bool _initialized;

    // 静止参考姿态：用于在"写世界旋转"时保留父级偏航与 FBX 轴向转换
    Quaternion _restBodyWorld = Quaternion.identity;
    float _restParentYaw;

    void Start()
    {
        CaptureHips();
    }

    /// <summary>车体在世界水平面里的"前"方向（抹掉俯仰），给移动逻辑和相机用。</summary>
    public Vector3 WorldForward()
    {
        Transform t = body != null ? body : transform;
        Vector3 localFwd = bodyForward.sqrMagnitude > 1e-6f ? bodyForward.normalized : Vector3.forward;
        Vector3 f = t.rotation * localFwd;
        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }

    /// <summary>层级改动后手动调用一次，重新记录髋点在车身空间的坐标。</summary>
    public void CaptureHips()
    {
        if (body == null || hips == null || hips.Length < 4) { _captured = false; return; }
        for (int i = 0; i < 4; ++i)
        {
            _hipLocal[i] = hips[i] != null ? body.InverseTransformPoint(hips[i].position) : Vector3.zero;
        }

        // 记录"静止参考姿态"：solver 每帧写的是世界旋转，必须知道基准才能把父级偏航保留下来
        _restBodyWorld = body.rotation;
        _restParentYaw = body.parent != null ? body.parent.rotation.eulerAngles.y : 0f;

        _captured = true;
    }

    void LateUpdate()
    {
        if (client == null || body == null || !_captured) return;

        BuildFeet();

        // 四足平面法线（车身空间），朝车体局部"上"
        Vector3 n = FitNormal();

        // 目标世界旋转 = 父级偏航增量 × 静止参考姿态 × 贴地倾角
        //
        // 为什么要带上前两项：本组件每帧写的是 **世界** 旋转，若直接赋
        // FromToRotation(n, worldUp)，就会把父级（空物体/模型根）的偏航一起抹掉 ——
        // 表现就是"空物体在转，狗却不动"。所以：
        //   · 父级偏航增量 → 保证 DogLocomotion 转根节点时狗跟着转
        //   · 静止参考姿态 → 保留 FBX 的 -90° 轴向转换，否则狗会躺下
        //   · 贴地倾角在 **局部空间** 右乘
        float parentYaw = body.parent != null ? body.parent.rotation.eulerAngles.y : 0f;
        Quaternion yawDelta = Quaternion.AngleAxis(Mathf.DeltaAngle(_restParentYaw, parentYaw), Vector3.up);

        Vector3 upLocal = bodyUp.sqrMagnitude > 1e-6f ? bodyUp.normalized : Vector3.up;
        Quaternion tilt = Quaternion.FromToRotation(n, upLocal);

        Quaternion q = yawDelta * _restBodyWorld * tilt;

        bool first = !_initialized;
        float t = first ? 1f : smoothing;

        // 先套旋转，再用 TransformPoint 求足端相对身体原点的世界高度
        //（这样父级的缩放/旋转都会算进去，Blender 导出的 scale=100 也没问题）
        body.rotation = Quaternion.Slerp(body.rotation, q, t);

        float minRelY = float.MaxValue;
        for (int i = 0; i < 4; ++i)
        {
            float rel = body.TransformPoint(_footLocal[i]).y - body.position.y;
            if (rel < minRelY) minRelY = rel;
        }
        if (minRelY == float.MaxValue) return;

        Vector3 pos = body.position;
        float targetWorldY = snapToGround ? groundY - minRelY : pos.y;
        pos.y = Mathf.Lerp(pos.y, targetWorldY, t);
        body.position = pos;

        _initialized = true;
    }

    void BuildFeet()
    {
        // 车体局部坐标系的"上/前"由 RigBaker 探测后写入；默认是 Unity 系（Y 上 / Z 前）。
        // Blender 直接导出的模型是 Z 上 / -Y 前，这时要靠这两个字段纠正。
        Vector3 up = bodyUp.sqrMagnitude > 1e-6f ? bodyUp.normalized : Vector3.up;
        Vector3 fwd = bodyForward.sqrMagnitude > 1e-6f ? bodyForward.normalized : Vector3.forward;

        for (int i = 0; i < 4; ++i)
        {
            float phi = client.LogicalDeg(i) * Mathf.Deg2Rad;
            // φ=0 → 沿 -up（竖直向下）；φ=±90 → 沿 ±forward
            Vector3 dir = -up * Mathf.Cos(phi) + fwd * (Mathf.Sin(phi) * swingSign);
            _footLocal[i] = _hipLocal[i] + dir * legLength;
        }
    }

    /// <summary>
    /// 四足平面的法线（车身空间，统一朝上）。
    ///
    /// ⚠️ 这里踩过一个坑，别再改回去：
    /// 足端数组顺序是 左前(0)、右前(1)、左后(2)、右后(3)。如果直接用
    ///   Cross(p1-p0, p2-p0) + Cross(p2-p0, p3-p0)
    /// 相当于按 0→1→2→3 切三角形，而 0→1→2→3 是一个"蝴蝶/自交"顺序
    /// （左前→右前→左后→右后），两个三角形的缠绕方向相反，法线会互相抵消。
    /// 四足严格共面时（stand/sleep）恰好抵消成 0，兜底回 Vector3.up 看着正常；
    /// 一旦穿上真实动作、四足不共面，抵消后剩下的残余法线是**水平**的，
    /// 于是 FromToRotation(n, up) 把车体翻 90°——表现就是"脚本来在地面上，
    /// 一动起来整个腿组跑到竖直平面里去了"。
    ///
    /// 正确做法：按沿地面绕一圈的顺序（左前 → 右前 → 右后 → 左后）做 Newell 积分，
    /// 它等价于所有按顺序切出的三角形法线之和，天然按面积加权，且与起点无关。
    /// </summary>
    Vector3 FitNormal()
    {
        // 沿地面一圈：LF → RF → RB → LB（注意下标是 0,1,3,2）
        int[] loop = { 0, 1, 3, 2 };

        Vector3 n = Vector3.zero;
        for (int k = 0; k < 4; ++k)
        {
            Vector3 a = _footLocal[loop[k]];
            Vector3 b = _footLocal[loop[(k + 1) & 3]];

            n.x += (a.y - b.y) * (a.z + b.z);
            n.y += (a.z - b.z) * (a.x + b.x);
            n.z += (a.x - b.x) * (a.y + b.y);
        }

        // 法线要跟车体局部空间的"上"比较（不能写死 +Y：Blender 系模型的"上"是 +Z）
        Vector3 upHint = bodyUp.sqrMagnitude > 1e-6f ? bodyUp.normalized : Vector3.up;

        if (n.sqrMagnitude < 1e-8f) return upHint;

        n = n.normalized;
        if (Vector3.Dot(n, upHint) < 0f) n = -n;

        // 兜底：法线几乎躺平说明姿态数据异常（或尺寸填错），别把狗翻过来
        if (Vector3.Dot(n, upHint) < 0.2f) return upHint;

        return n;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || body == null || hips == null) return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < 4; ++i)
        {
            if (hips[i] == null) continue;
            Gizmos.DrawWireSphere(hips[i].position, legLength * 0.12f);
        }

        if (!Application.isPlaying || !_captured) return;
        Gizmos.color = Color.cyan;
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < 4; ++i)
        {
            // body 上已经是当前（平滑后）的姿态，直接用它换算足端世界坐标
            Vector3 foot = body.TransformPoint(_footLocal[i]);
            Vector3 hip = body.TransformPoint(_hipLocal[i]);
            Gizmos.DrawSphere(foot, legLength * 0.08f);
            Gizmos.DrawLine(hip, foot);
            centroid += foot * 0.25f;
        }

        // 品红射线 = 拟合出的四足平面法线。它必须接近竖直；如果它躺平了，
        // 说明足端顺序 / 尺寸填错，车体会被翻 90°。
        Gizmos.color = Color.magenta;
        Gizmos.DrawRay(centroid, body.rotation * FitNormal() * (legLength * 1.2f));
    }
}
