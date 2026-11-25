// DogLocomotion.cs —— 让虚拟狗真的"走"和"转"
//
// 为什么需要：遥测里只有腿角度、动作名和 drive 方向，**没有朝向（yaw）也没有位置**。
// 真狗的 turn_left/turn_right 是靠对角步态拧出来的偏航，但孪生这边只镜像了关节角度，
// 所以不积分 yaw 的话，虚拟狗只会原地摆腿 + 身体侧倾，看起来像"左右平移"。
//
// 本脚本按 drive / action 积分根节点的朝向与位移。速度按 **腿长** 归一化，
// 所以模型放大缩小都不用改参数。
//
// 用法：挂在狗的根节点（CaiyuDog）上，把 client / solver 拖进来即可。

using UnityEngine;

[DisallowMultipleComponent]
public class DogLocomotion : MonoBehaviour
{
    [Header("引用")]
    public DogClient client;
    [Tooltip("用来取腿长做速度归一化，也用来判断车体朝向。可留空")]
    public DogBodySolver solver;

    [Header("速度（按腿长归一化）")]
    [Tooltip("每秒走多少个腿长。1.0 ≈ 每腿长 1 秒")]
    public float walkLegsPerSecond = 1.6f;
    [Tooltip("转向速度，度/秒")]
    public float turnRate = 110f;
    [Tooltip("单步动作（walk / walk_back）也带位移吗")]
    public bool moveOnWalkActions = true;
    [Tooltip("单步动作的速度倍率")]
    public float actionSpeedScale = 0.8f;

    [Header("行为")]
    [Tooltip("离线模拟（DogSimulator）时是否也接管移动。默认交给模拟器")]
    public bool enableInSimulation;

    [Header("状态（只读）")]
    [SerializeField] private float _yaw;
    [SerializeField] private float _speedNow;

    Vector3 _heading = Vector3.forward;
    Vector3 _pivotLocal;
    bool _headingInit;

    public float Yaw => _yaw;
    public Vector3 Heading => _heading;

    void Start()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (solver == null) solver = GetComponent<DogBodySolver>();
        _yaw = transform.eulerAngles.y;
        ResolveHeading();
        _pivotLocal = DogPivot.ComputePivotLocal(transform);
    }

    [ContextMenu("重新计算旋转中心")]
    void RecomputePivot()
    {
        _pivotLocal = DogPivot.ComputePivotLocal(transform);
        Debug.Log($"[菜鱼狗] 旋转中心已重算：本地偏移 {_pivotLocal}");
    }

    /// <summary>车体在世界水平面里的"前"方向（由 solver 的 bodyForward 换算，避开 FBX 轴向差异）</summary>
    void ResolveHeading()
    {
        Vector3 f;
        if (solver != null)
        {
            f = solver.WorldForward();
        }
        else
        {
            f = transform.rotation * Vector3.forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        if (!_headingInit)
        {
            _heading = f;
            _headingInit = true;
        }
        // 已初始化时不覆盖：_heading 由 LateUpdate 按每次转向增量维护
    }

    void LateUpdate()
    {
        _speedNow = 0f;
        if (client == null) return;
        if (!enableInSimulation && client.offlineSimulation) return;   // 模拟器自己会动

        DogTelemetry t = client.Latest;
        if (t == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float legLen = (solver != null && solver.legLength > 1e-6f) ? solver.legLength : 1f;

        string drive = string.IsNullOrEmpty(t.drive) ? "none" : t.drive;
        bool busy = t.busy;
        string action = t.action ?? "";

        float yawDelta = 0f;
        float advance = 0f;

        // 摇杆（连续）
        if (drive == "left") yawDelta = -turnRate * dt;
        else if (drive == "right") yawDelta = turnRate * dt;
        else if (drive == "forward") advance = walkLegsPerSecond * legLen * dt;
        else if (drive == "backward") advance = -walkLegsPerSecond * legLen * dt;

        // 单步动作（手机网页点一次 / 语音指令）
        if (yawDelta == 0f && advance == 0f && busy)
        {
            if (action == "turn_left") yawDelta = -turnRate * dt;
            else if (action == "turn_right") yawDelta = turnRate * dt;
            else if (moveOnWalkActions && action == "walk")
                advance = walkLegsPerSecond * actionSpeedScale * legLen * dt;
            else if (moveOnWalkActions && action == "walk_back")
                advance = -walkLegsPerSecond * actionSpeedScale * legLen * dt;
        }

        if (yawDelta != 0f)
        {
            _yaw += yawDelta;
            // 绕**模型可视中心**转（不是绕物体原点）：后者会让整只狗绕着空气打转。
            // Spin 内部前乘世界 Y 轴，并把根节点位置一起修正。
            DogPivot.Spin(transform, _pivotLocal, yawDelta);

            _heading = Quaternion.AngleAxis(yawDelta, Vector3.up) * _heading;
            _heading = DogPivot.Horizontal(_heading, Vector3.forward);
        }

        if (advance != 0f)
        {
            transform.position += _heading * advance;
            _speedNow = advance / dt;
        }
    }

    [ContextMenu("重置朝向为世界前方")]
    void ResetHeading()
    {
        transform.rotation = Quaternion.identity;
        _yaw = 0f;
        _heading = Vector3.forward;
        _headingInit = true;
    }
}
