// DogSceneSetup.cs —— 一键生成"能看"的场景环境（地面 + 双光源 + 渐变天空）
//
// 为什么做成运行时组件而不是 Editor 菜单：
//   打包成 exe 后场景也是自动生成的，别人拿到就直接有环境，
//   不会出现"白底悬浮模型"的廉价感。
//
// 视觉设计（比第一版耐看）：
//   ① 主光：暖白、左上前方、柔和阴影          → 塑造体积
//   ② 轮廓光：冷蓝、右后上方、不投影          → 勾出边缘，和主光形成冷暖对比
//   ③ 渐变天空：内置 Procedural Skybox         → 取代纯色背景，有"棚拍"感
//   ④ 地面压暗 + 细网格                        → 提供尺度参照又不抢主体
//
// 用法：挂在 CaiyuDog 上，Play 时自动补齐缺失的环境物体（同名物体不会重复创建）。

using UnityEngine;

[DisallowMultipleComponent]
public class DogSceneSetup : MonoBehaviour
{
    [Header("开关")]
    public bool createGround = true;
    public bool createKeyLight = true;
    public bool createRimLight = true;
    public bool createSky = true;
    public bool tintCamera = true;

    [Header("地面")]
    [Tooltip("地面边长（世界单位）。狗只有 0.06 单位长的话，8~20 都行")]
    public float groundSize = 12f;
    public Color groundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
    [Tooltip("网格间距（世界单位）。狗 0.06 单位长的话 0.1 左右；0 = 不画网格")]
    public float gridStep = 0.1f;
    public Color gridColor = new Color(1f, 1f, 1f, 0.045f);

    [Header("主光（暖）")]
    public Vector3 keyLightEuler = new Vector3(52f, -35f, 0f);
    [Range(0f, 4f)] public float keyIntensity = 1.25f;
    [Range(0f, 1f)] public float keyShadowStrength = 0.55f;
    public Color keyColor = new Color(1f, 0.96f, 0.90f, 1f);

    [Header("轮廓光（冷，不投影）")]
    public Vector3 rimLightEuler = new Vector3(-35f, 145f, 0f);
    [Range(0f, 3f)] public float rimIntensity = 0.75f;
    public Color rimColor = new Color(0.55f, 0.72f, 1f, 1f);

    [Header("环境光")]
    [Range(0f, 2f)] public float ambientIntensity = 0.5f;
    public Color ambientColor = new Color(0.40f, 0.46f, 0.58f, 1f);

    [Header("天空（渐变）")]
    public Color skyTint = new Color(0.22f, 0.30f, 0.45f, 1f);
    public Color groundBounce = new Color(0.10f, 0.11f, 0.14f, 1f);
    [Range(0.1f, 3f)] public float skyExposure = 1.15f;
    [Range(0f, 5f)] public float atmosphereThickness = 1.0f;

    [Header("相机")]
    public Color cameraBackground = new Color(0.09f, 0.10f, 0.13f, 1f);

    void Awake()
    {
        // 环境光：Flat 模式 + 偏冷灰蓝，让暗部不死黑
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = ambientColor * ambientIntensity;

        if (createSky) EnsureSky();
        if (createGround) EnsureGround();
        if (createKeyLight) EnsureKeyLight();
        if (createRimLight) EnsureRimLight();
        if (tintCamera) TintCamera();
    }

    // ------------------------------------------------------------------ 天空

    void EnsureSky()
    {
        Shader sh = Shader.Find("Skybox/Procedural");
        if (sh == null) return;

        var mat = new Material(sh);
        mat.name = "DogSky";
        if (mat.HasProperty("_SkyTint")) mat.SetColor("_SkyTint", skyTint);
        if (mat.HasProperty("_GroundColor")) mat.SetColor("_GroundColor", groundBounce);
        if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", skyExposure);
        if (mat.HasProperty("_AtmosphereThickness")) mat.SetFloat("_AtmosphereThickness", atmosphereThickness);

        RenderSettings.skybox = mat;
        RenderSettings.sun = FindKeyLight();
    }

    // ------------------------------------------------------------------ 地面

    void EnsureGround()
    {
        if (GameObject.Find("CaiyuGround") != null) return;

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = "CaiyuGround";
        go.transform.SetParent(transform.parent, false);
        go.transform.position = Vector3.zero;
        go.transform.localScale = Vector3.one * (groundSize / 10f);

        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        Texture2D grid = null;
        float tiling = 0f;
        if (gridStep > 0f)
        {
            grid = BuildGridTexture(128, gridColor, groundColor);
            tiling = groundSize / gridStep;
        }

        Material mat = CreateLitMaterial(groundColor, grid, tiling);
        if (mat != null) r.sharedMaterial = mat;
    }

    // ------------------------------------------------------------------ 灯光

    Light FindKeyLight()
    {
        GameObject g = GameObject.Find("CaiyuKeyLight");
        return g != null ? g.GetComponent<Light>() : null;
    }

    void EnsureKeyLight()
    {
        if (GameObject.Find("CaiyuKeyLight") != null) return;

        // 兼容旧场景：之前版本生成的光叫 CaiyuSun，这里直接复用并改名，
        // 避免出现"旧光 + 新主光 + 轮廓光"三盏方向灯把画面打爆。
        GameObject go = GameObject.Find("CaiyuSun");
        if (go == null)
        {
            go = new GameObject("CaiyuKeyLight");
            go.transform.SetParent(transform.parent, false);
        }
        else
        {
            go.name = "CaiyuKeyLight";
        }

        go.transform.rotation = Quaternion.Euler(keyLightEuler);

        Light l = go.GetComponent<Light>();
        if (l == null) l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = keyColor;
        l.intensity = keyIntensity;
        l.shadows = LightShadows.Soft;
        l.shadowStrength = keyShadowStrength;
        l.shadowBias = 0.02f;
        l.shadowNormalBias = 0.2f;
    }

    void EnsureRimLight()
    {
        if (GameObject.Find("CaiyuRimLight") != null) return;

        GameObject go = new GameObject("CaiyuRimLight");
        go.transform.SetParent(transform.parent, false);
        go.transform.rotation = Quaternion.Euler(rimLightEuler);

        Light l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = rimColor;
        l.intensity = rimIntensity;
        // 轮廓光刻意不投影：只负责勾边，投影交给主光，避免双重阴影
        l.shadows = LightShadows.None;
    }

    void TintCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = cameraBackground;
    }

    // ------------------------------------------------------------------ 材质与贴图

    /// <summary>带贴图的受光材质（URP 和内置管线都尽量兼容）</summary>
    static Material CreateLitMaterial(Color c, Texture2D tex, float tiling)
    {
        string[] candidates =
        {
            "Universal Render Pipeline/Lit",   // URP
            "Standard",                        // 内置管线
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
        };

        foreach (string name in candidates)
        {
            Shader sh = Shader.Find(name);
            if (sh == null) continue;

            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);

            if (tex != null)
            {
                if (m.HasProperty("_BaseMap"))
                {
                    m.SetTexture("_BaseMap", tex);
                    m.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
                }
                if (m.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", tex);
                    m.SetTextureScale("_MainTex", new Vector2(tiling, tiling));
                }
                // 受光材质的地面反光调低，避免网格上一片高光
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.08f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.08f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            }
            return m;
        }
        return null;
    }

    /// <summary>生成一张"填充色 + 细网格线"的可平铺贴图</summary>
    static Texture2D BuildGridTexture(int size, Color lineColor, Color fillColor)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name = "CaiyuGroundGrid";
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;

        var px = new Color32[size * size];
        Color32 line = lineColor;
        Color32 fill = fillColor;
        const int lineWidth = 1;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                bool onLine = x < lineWidth || y < lineWidth ||
                              x >= size - lineWidth || y >= size - lineWidth;
                px[y * size + x] = onLine ? line : fill;
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }
}
