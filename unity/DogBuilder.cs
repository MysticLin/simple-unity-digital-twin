// DogBuilder.cs —— 一键生成"菜鱼狗"孪生骨架（Editor 工具）
//
// 菜单：Tools ▸ 菜鱼狗 ▸ 生成孪生骨架
//
// 它按真狗的机械结构用基本体拼出整只狗，并把三个脚本和引用全部接好：
//   车体(方块) + 4 个髋关节(空物体, 横向轴) + 4 条腿(竖直向下的方块) + OLED 屏幕(Quad) + 两条灯带
//
// 单位约定：默认 1 unit = 1 cm（真狗约 8 unit 长），这样量出来的毫米数可以直接填。
// 全部参数都可以改；改完重新生成即可。
//
// 本文件只用到 UnityEditor 的部分都包在 #if UNITY_EDITOR 里，放在 Assets 下任意位置都能编译。

using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class DogBuilder : MonoBehaviour
{
    [Header("车体（单位：默认 cm）")]
    public float bodyLength = 8.0f;    // Z
    public float bodyWidth = 5.0f;     // X
    public float bodyHeight = 2.0f;    // Y
    public float bodyCenterY = 0f;     // 车体中心相对根节点的高度

    [Header("髋关节位置（相对车体中心）")]
    public float hipX = 2.4f;          // 左右半轮距
    public float hipZ = 3.0f;          // 前后半轴距
    public float hipY = -0.2f;         // 舵机轴相对车体中心的高度

    [Header("腿")]
    public float legLength = 2.8f;     // 髋轴 → 足底（用尺子量）
    public float legThickness = 0.5f;
    public float footThickness = 0.35f;

    [Header("屏幕（OLED 128×64，2:1）")]
    public float screenWidth = 2.6f;
    public float screenHeight = 1.3f;

    [Header("灯带")]
    public bool createStrips = true;
    public float stripThickness = 0.25f;

    [Header("外观")]
    public Color bodyColor = new Color(0.18f, 0.19f, 0.22f);
    public Color legColor = new Color(0.10f, 0.10f, 0.12f);
    public Color screenColor = Color.black;

    /// <summary>生成整只狗并返回根节点。</summary>
    public GameObject Build()
    {
        var root = new GameObject("CaiyuDog");

        // ---------------- 车体 ----------------
        var body = CreateBox("Body", root.transform, bodyColor);
        body.transform.localPosition = new Vector3(0f, bodyCenterY, 0f);
        body.transform.localScale = new Vector3(bodyWidth, bodyHeight, bodyLength);

        // ---------------- 四条腿 ----------------
        // 顺序必须与固件一致：左前、右前、左后、右后
        string[] names = { "LF", "RF", "LB", "RB" };
        float[] signX = { -1f, 1f, -1f, 1f };
        float[] signZ = { 1f, 1f, -1f, -1f };

        var hips = new Transform[4];
        var legs = new Transform[4];

        for (int i = 0; i < 4; ++i)
        {
            // 髋关节：空物体，旋转轴 = 本地 X（横向），腿挂在它下面
            var hip = new GameObject("Hip" + names[i]);
            hip.transform.SetParent(body.transform, false);
            hip.transform.localPosition = new Vector3(signX[i] * hipX, hipY, signZ[i] * hipZ);
            hip.transform.localRotation = Quaternion.identity;
            hips[i] = hip.transform;

            // 腿：从髋轴竖直向下
            var leg = CreateBox("Leg" + names[i], hip.transform, legColor);
            leg.transform.localPosition = new Vector3(0f, -legLength * 0.5f, 0f);
            leg.transform.localScale = new Vector3(legThickness, legLength, legThickness);

            // 注意：交给 DogClient 旋转的就是髋关节本身（枢轴 = 舵机轴），不是腿的网格
            legs[i] = hip.transform;

            // 脚垫
            var foot = CreateBox("Foot" + names[i], hip.transform, legColor);
            foot.transform.localPosition = new Vector3(0f, -legLength + footThickness * 0.5f, 0f);
            foot.transform.localScale = new Vector3(legThickness * 1.4f, footThickness, legThickness * 2.4f);
        }

        // ---------------- 屏幕 ----------------
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "Screen";
        SafeDestroy(screen.GetComponent<Collider>());
        screen.transform.SetParent(body.transform, false);
        screen.transform.localPosition = new Vector3(0f, 0f, bodyLength * 0.5f + 0.02f);
        // Quad 默认朝 -Z，翻 180° 让它朝 +Z（狗头方向）
        screen.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        screen.transform.localScale = new Vector3(screenWidth, screenHeight, 1f);

        var screenRenderer = screen.GetComponent<Renderer>();
        screenRenderer.sharedMaterial = CreateUnlitMaterial(screenColor);

        // ---------------- 两条灯带 ----------------
        var strips = new Renderer[0];
        if (createStrips)
        {
            strips = new Renderer[2];
            for (int i = 0; i < 2; ++i)
            {
                var strip = CreateBox("Strip" + (i == 0 ? "L" : "R"), body.transform,
                                      new Color(0.05f, 0.05f, 0.06f));
                strip.transform.localPosition =
                    new Vector3((i == 0 ? -1f : 1f) * (bodyWidth * 0.5f + stripThickness * 0.5f),
                                bodyHeight * 0.1f, 0f);
                strip.transform.localScale =
                    new Vector3(stripThickness, stripThickness, bodyLength * 0.85f);
                strips[i] = strip.GetComponent<Renderer>();
            }
        }

        // ---------------- 挂脚本 + 接线 ----------------
        var client = root.AddComponent<DogClient>();
        client.legRoots = legs;
        client.body = null;                 // 交由 DogBodySolver 接管
        client.bodyTiltGain = 0f;

        var solver = root.AddComponent<DogBodySolver>();
        solver.client = client;
        solver.body = body.transform;
        solver.hips = hips;
        solver.legLength = legLength;

        var face = root.AddComponent<DogFaceTexture>();
        face.client = client;
        face.screenRenderer = screenRenderer;

        Debug.Log("[菜鱼狗] 骨架已生成。下一步：\n" +
                  "1) 把 DogClient.deviceHost 改成设备 IP\n" +
                  "2) 用键盘 WASD（勾选 enableKeyboardDebug）或手机网页先验证动作\n" +
                  "3) 若前后走反了，把 DogBodySolver.swingSign 改成 -1\n" +
                  "4) 若左右腿镜像不对，改 DogClient.rightMirrorSign");

        return root;
    }

    static GameObject CreateBox(string name, Transform parent, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        SafeDestroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().sharedMaterial = CreateLitMaterial(color);
        return go;
    }

    static Material CreateLitMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Diffuse");

        var mat = new Material(shader) { name = "CaiyuPart" };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        return mat;
    }

    static Material CreateUnlitMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        var mat = new Material(shader) { name = "CaiyuScreen" };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        return mat;
    }

    static void SafeDestroy(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}

#if UNITY_EDITOR
static class DogMenu
{
    [MenuItem("Tools/菜鱼狗/生成孪生骨架")]
    static void CreateDog()
    {
        var host = new GameObject("DogBuilder");
        var builder = host.AddComponent<DogBuilder>();
        GameObject dog = builder.Build();
        Undo.RegisterCreatedObjectUndo(dog, "Create CaiyuDog");
        Object.DestroyImmediate(host);   // 静态类不继承 Object，必须限定
        Selection.activeGameObject = dog;
    }

    [MenuItem("Tools/菜鱼狗/重新记录髋点坐标")]
    static void RecaptureHips()
    {
        var solver = Object.FindObjectOfType<DogBodySolver>();
        if (solver == null)
        {
            Debug.LogWarning("[菜鱼狗] 场景里没有 DogBodySolver");
            return;
        }
        solver.CaptureHips();
        Debug.Log("[菜鱼狗] 髋点已重新记录");
    }

    [MenuItem("Tools/菜鱼狗/切换离线模拟（没有硬件也能动）")]
    static void ToggleOfflineSimulation()
    {
        var client = Object.FindObjectOfType<DogClient>();
        if (client == null)
        {
            Debug.LogWarning("[菜鱼狗] 场景里没有 DogClient，先生成骨架");
            return;
        }

        var sim = client.GetComponent<DogSimulator>();
        bool toSim = sim == null || !client.offlineSimulation;

        if (toSim)
        {
            client.offlineSimulation = true;
            client.enableKeyboardDebug = false;   // 键盘交给模拟器
            if (sim == null) sim = client.gameObject.AddComponent<DogSimulator>();
            sim.client = client;
            Debug.Log("[菜鱼狗] 已切到【离线模拟】：进 Play 后 W/A/S/D 走动，" +
                      "1-9 切动作（1站 2坐 3睡 4趴 5挥手 6走 7退 8左转 9右转），Q/E/R/F 换表情");
        }
        else
        {
            client.offlineSimulation = false;
            if (sim != null) Object.DestroyImmediate(sim);
            Debug.Log("[菜鱼狗] 已切到【真机镜像】：把 deviceHost 填成设备 IP，" +
                      "并确认固件已刷入带 telemetry 的版本");
        }
    }
}
#endif
