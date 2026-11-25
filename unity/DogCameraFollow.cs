// DogCameraFollow.cs —— 让相机平滑跟随狗，自动适配任意模型尺寸
//
// 模型可能很小（Blender 导出的 6.4cm = 0.064 单位）也可能很大（未做单位换算的 640 单位），
// 所以相机距离不是写死的，而是按**目标包围球半径**推算：距离 = 半径 × distanceFactor。
//
// 用法：挂在 Main Camera 上，把狗根节点拖到 target。

using UnityEngine;

[DisallowMultipleComponent]
public class DogCameraFollow : MonoBehaviour
{
    [Header("引用")]
    public Transform target;
    [Tooltip("可留空；填了就能知道狗头朝哪，转弯时相机可以跟着绕")]
    public DogBodySolver solver;
    [Tooltip("可留空；填了用它的实时朝向（推荐，转弯后相机能绕到背后）")]
    public DogLocomotion locomotion;

    [Header("构图")]
    [Tooltip("相机到目标的距离 = 包围球半径 × 这个系数")]
    [Range(1f, 10f)] public float distanceFactor = 3.0f;
    [Tooltip("抬高比例：相机高度 = 距离 × 这个值")]
    [Range(0f, 1.2f)] public float heightRatio = 0.45f;
    [Tooltip("看向目标中心还是脚底")]
    [Range(-1f, 1f)] public float lookHeightRatio = 0.05f;

    [Header("行为")]
    [Tooltip("跟随朝向：转弯时相机跟着绕到背后；关掉则固定方位（看静止参照用）")]
    public bool followHeading = false;
    [Tooltip("平滑速度，越大越跟手")]
    [Range(0.5f, 30f)] public float smooth = 8f;
    public bool lookAtTarget = true;

    [Header("手动微调")]
    [Tooltip("鼠标右键拖动：绕目标水平旋转（度）")]
    public bool allowManualOrbit = true;
    public float orbitSensitivity = 3f;
    [Tooltip("滚轮缩放：改变距离系数")]
    public float zoomSensitivity = 0.15f;

    Vector3 _fixedHeading = Vector3.forward;
    bool _fixedInit;
    float _manualYaw;
    float _manualPitch = 18f;
    float _zoom = 1f;
    Vector3 _pos;
    bool _posInit;

    void LateUpdate()
    {
        if (target == null) return;

        // ---- 目标包围球 ----
        bool any = false;
        Bounds b = new Bounds(target.position, Vector3.zero);
        foreach (Renderer r in target.GetComponentsInChildren<Renderer>())
        {
            if (r == null) continue;
            if (!any) { b = r.bounds; any = true; }
            else { b.Encapsulate(r.bounds.min); b.Encapsulate(r.bounds.max); }
        }
        if (!any) b = new Bounds(target.position, Vector3.one);

        Vector3 center = b.center;
        float radius = Mathf.Max(1e-4f, b.extents.magnitude);

        // ---- 方位 ----
        if (!followHeading)
        {
            if (!_fixedInit)
            {
                _fixedHeading = Heading();
                _fixedInit = true;
            }
        }
        Vector3 heading = followHeading ? Heading() : _fixedHeading;
        if (heading.sqrMagnitude < 1e-6f) heading = Vector3.forward;

        // ---- 手动环绕 ----
        if (allowManualOrbit)
        {
            if (Input.GetMouseButton(1))
            {
                _manualYaw += Input.GetAxis("Mouse X") * orbitSensitivity;
                _manualPitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
                _manualPitch = Mathf.Clamp(_manualPitch, -80f, 80f);
            }

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 1e-4f)
            {
                _zoom = Mathf.Clamp(_zoom * (1f - wheel * zoomSensitivity), 0.25f, 6f);
            }
        }

        Quaternion orbit = Quaternion.Euler(_manualPitch, _manualYaw, 0f);
        float dist = radius * distanceFactor * _zoom;

        // 默认落在目标"背后上方"：-heading 再叠加手动环绕
        Vector3 back = -heading;
        Vector3 up = Vector3.up;
        Vector3 want = center
                       + (orbit * back) * dist
                       + up * (dist * heightRatio);

        float k = 1f - Mathf.Pow(0.001f, Time.deltaTime * smooth);
        if (!_posInit) { _pos = want; _posInit = true; }
        _pos = Vector3.Lerp(_pos, want, k);
        transform.position = _pos;

        if (lookAtTarget)
        {
            Vector3 look = center + Vector3.up * (radius * lookHeightRatio);
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(look - transform.position), k);
        }
    }

    Vector3 Heading()
    {
        // 优先用 locomotion 的实时朝向：solver 的 body.rotation 是"贴地姿态"，
        // 不含根节点上的偏航，光靠它是不会跟着转弯的
        if (locomotion != null)
        {
            Vector3 h = locomotion.Heading;
            h.y = 0f;
            if (h.sqrMagnitude > 1e-6f) return h.normalized;
        }

        Vector3 f = solver != null
            ? solver.WorldForward()
            : transform.rotation * Vector3.forward;

        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }

    [ContextMenu("重置视角")]
    void ResetView()
    {
        _manualYaw = 0f;
        _manualPitch = 18f;
        _zoom = 1f;
        _fixedInit = false;
        _posInit = false;
    }
}
