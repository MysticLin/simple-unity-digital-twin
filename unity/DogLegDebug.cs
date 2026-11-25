// DogLegDebug.cs —— 把四条腿的实际朝向打成数字，用来判断左右是否对称
//
// 为什么需要它：
//   肉眼看 3D 模型的腿很容易被视角骗；而"坐下/睡觉"是否对称，本质是
//   "左右两条腿在车体坐标系里的方向（y/z 分量）是否相同"。
//   这个脚本每 0.5 秒把这四个方向打出来，一眼就能看出谁不对。
//
// 用法：挂在 CaiyuDog 上（client / body 会自动找），Play 后看 Console。
//       读法：比较 LF 与 RF 的 (y, z)，以及 LB 与 RB 的 (y, z)。
//       两两相同 → 对称；不同 → 那条腿有问题。

using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public class DogLegDebug : MonoBehaviour
{
    [Header("引用（留空自动找）")]
    public DogClient client;
    public DogBodySolver solver;
    public Transform body;
    public Transform[] legs = new Transform[4];

    [Header("输出")]
    [Range(0.2f, 3f)] public float interval = 0.5f;
    public bool printAngles = true;

    static readonly string[] Names = { "LF", "RF", "LB", "RB" };

    float _acc;
    Vector3[] _restLocalDir = new Vector3[4];
    bool _restCaptured;

    void Start()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (solver == null) solver = GetComponent<DogBodySolver>();

        if (body == null && solver != null) body = solver.body;
        if (body == null) body = transform;

        if (legs == null || legs.Length < 4 || legs[0] == null) legs = FindLegs();
    }

    /// <summary>按名字找 leg-front-left / leg-front-right / leg-back-left / leg-back-right</summary>
    Transform[] FindLegs()
    {
        var found = new Transform[4];
        string[] wanted = { "leg-front-left", "leg-front-right", "leg-back-left", "leg-back-right" };

        foreach (Transform t in body.GetComponentsInChildren<Transform>())
        {
            string n = t.name.ToLowerInvariant();
            for (int i = 0; i < 4; ++i)
            {
                if (found[i] == null && n == wanted[i]) { found[i] = t; break; }
            }
        }
        return found;
    }

    /// <summary>腿的朝向：从枢轴指向它下面的轮子（没有轮子就用第一个子物体）</summary>
    bool LegDirection(int i, out Vector3 bodyLocalDir)
    {
        bodyLocalDir = Vector3.zero;
        if (legs == null || i >= legs.Length || legs[i] == null) return false;

        Transform leg = legs[i];
        Transform tip = null;

        foreach (Transform c in leg) { tip = c; break; }          // 默认第一个子物体
        foreach (Transform c in leg.GetComponentsInChildren<Transform>())
        {
            if (c == leg) continue;
            if (c.name.ToLowerInvariant().Contains("wheel")) { tip = c; break; }
        }
        if (tip == null) return false;

        Vector3 world = tip.position - leg.position;
        if (world.sqrMagnitude < 1e-12f) return false;

        bodyLocalDir = body.InverseTransformDirection(world.normalized);
        return true;
    }

    void Update()
    {
        if (client == null || legs == null || legs.Length < 4) return;

        _acc += Time.deltaTime;
        if (_acc < interval) return;
        _acc = 0f;

        if (!_restCaptured)
        {
            for (int i = 0; i < 4; ++i)
                if (LegDirection(i, out Vector3 d)) _restLocalDir[i] = d;
            _restCaptured = true;
        }

        DogTelemetry t = client.Latest;
        var sb = new StringBuilder();
        sb.AppendLine("[腿诊断] （方向 = 车体局部空间里 枢轴→轮子 的归一化向量）");
        sb.AppendLine("              φ      dir(x, y, z)              与LF的偏差");

        Vector3 reference = Vector3.zero;

        for (int i = 0; i < 4; ++i)
        {
            if (legs[i] == null) { sb.AppendLine($"  {Names[i]}  <未找到>"); continue; }
            if (!LegDirection(i, out Vector3 d)) { sb.AppendLine($"  {Names[i]}  量不出方向"); continue; }

            float phi = 0f;
            if (t != null && t.legs != null && t.legs.Length > i)
                phi = t.legs[i] + ((t.trims != null && t.trims.Length > i) ? t.trims[i] : 0) - 90f;

            // 左右对称的判据：把 x 取绝对值后，y/z 应该一致
            if (i == 0) reference = new Vector3(Mathf.Abs(d.x), d.y, d.z);
            Vector3 cmp = new Vector3(Mathf.Abs(d.x), d.y, d.z);
            float diff = Vector3.Distance(cmp, reference);

            sb.AppendLine($"  {Names[i]}  φ={phi,6:F1}   ({d.x,6:F3},{d.y,6:F3},{d.z,6:F3})   {diff:F3}" +
                          (printAngles ? $"   euler={legs[i].localEulerAngles.ToString()}" : ""));
        }

        sb.AppendLine("→ 每行最后的数字是「与 LF 的偏差」：LF/RF 应≈0，LB/RB 也应≈0（左右对称）");
        Debug.Log(sb.ToString());
    }
}
