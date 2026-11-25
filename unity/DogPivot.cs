// DogPivot.cs —— 绕"模型自己的可视中心"旋转，而不是绕物体原点
//
// 为什么需要：Unity 的 transform.rotation 是绕物体**原点**转的。
// 而 Blender 导出的模型原点常常不在狗的可视中心（根节点带偏移 + -90° 轴向转换），
// 直接转 rotation 会让整只狗绕着空气里的一个点打转，看起来像"公转"而不是"自转"。
//
// 做法：先量出模型水平面内的可视中心（相对根节点的本地偏移，只在初始化时算一次），
// 旋转时把根节点位置一起修正，让那个中心保持不动。

using UnityEngine;

public static class DogPivot
{
    /// <summary>模型可视中心相对 root 的本地偏移（y 归零，只在水平面内）</summary>
    public static Vector3 ComputePivotLocal(Transform root)
    {
        if (root == null) return Vector3.zero;

        bool any = false;
        Bounds b = new Bounds(root.position, Vector3.zero);
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
        {
            if (r == null) continue;
            if (!any) { b = r.bounds; any = true; }
            else { b.Encapsulate(r.bounds.min); b.Encapsulate(r.bounds.max); }
        }
        if (!any) return Vector3.zero;

        Vector3 local = root.InverseTransformPoint(b.center);
        local.y = 0f;          // 垂直方向不参与旋转中心，避免上下漂
        return local;
    }

    /// <summary>绕模型中心在水平面内旋转 degrees 度（正数 = 世界 Y 轴逆时针）</summary>
    public static void Spin(Transform root, Vector3 pivotLocal, float degrees)
    {
        if (root == null || Mathf.Abs(degrees) < 1e-6f) return;

        Quaternion r = Quaternion.AngleAxis(degrees, Vector3.up);

        // 当前模型中心（世界坐标），高度取根节点高度
        Vector3 pivotWorld = root.position + root.rotation * pivotLocal;
        pivotWorld.y = root.position.y;

        // 绕 pivotWorld 旋转：原点跟着绕，模型中心保持不动
        root.position = pivotWorld + r * (root.position - pivotWorld);
        root.rotation = r * root.rotation;   // 前乘 = 绕世界 Y 轴
    }

    /// <summary>把方向抹平到水平面并归一化</summary>
    public static Vector3 Horizontal(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
    }

    /// <summary>模型的世界"前"方向（优先用 solver 的 bodyForward，绕过 FBX 轴向差异）</summary>
    public static Vector3 ForwardOf(Transform root, DogBodySolver solver)
    {
        if (solver != null)
        {
            Vector3 f = solver.WorldForward();
            f.y = 0f;
            if (f.sqrMagnitude > 1e-6f) return f.normalized;
        }

        Vector3 fallback = root != null ? root.rotation * Vector3.forward : Vector3.forward;
        return Horizontal(fallback, Vector3.forward);
    }
}
