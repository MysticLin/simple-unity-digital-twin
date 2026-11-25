// DogBlobShadow.cs —— 跟随狗的水平软阴影（blob shadow）
//
// 为什么用假阴影而不是真阴影贴图：
//   · 狗只有 0.06 世界单位长的时候，真实阴影贴图要么糊要么锯齿
//   · 假阴影零性能开销，而且可以随"离地高度"变虚，视觉上更像回事
//
// 用法：挂在 CaiyuDog 上，Play 时自动在脚下生成一个跟随的软影。
//       半径会按模型的包围球自动适配，所以模型放大缩小都不用改参数。

using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class DogBlobShadow : MonoBehaviour
{
    [Header("引用（可留空，自动找）")]
    public DogClient client;
    public DogBodySolver solver;

    [Header("外观")]
    [Tooltip("阴影半径 = 目标包围球半径 × 这个系数")]
    [Range(0.5f, 3f)] public float radiusFactor = 1.15f;
    public Color shadowColor = new Color(0f, 0f, 0f, 0.5f);
    public float groundY = 0f;
    [Tooltip("贴地偏移，按阴影半径的比例算（模型缩放后自动适配，不会悬空）")]
    [Range(0.001f, 0.1f)] public float groundBias = 0.02f;

    [Header("随高度变化")]
    [Tooltip("狗离地越高，影子越大越淡（0 = 完全不变）")]
    [Range(0f, 2f)] public float heightInfluence = 0.6f;
    [Tooltip("超过这个高度就完全淡出（自动按模型尺寸放大，小于 半径×4 时按 半径×4 算）")]
    public float fadeHeight = 1.5f;

    [Header("贴图")]
    [Range(16, 128)] public int textureSize = 64;
    [Tooltip("边缘柔和程度，越大越虚")]
    [Range(0.4f, 4f)] public float softness = 1.8f;

    GameObject _quad;
    Renderer _renderer;
    Material _material;
    Texture2D _texture;
    float _radius = 0.5f;
    bool _radiusResolved;

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
        if (solver == null) solver = GetComponent<DogBodySolver>();

        _texture = BuildFalloffTexture(textureSize, softness);
        CreateQuad();
    }

    void OnDestroy()
    {
        if (_quad != null) Destroy(_quad);
        if (_material != null) Destroy(_material);
        if (_texture != null) Destroy(_texture);
    }

    void CreateQuad()
    {
        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "DogBlobShadow";
        _quad.transform.SetParent(null, false);      // 不跟着狗转，永远水平
        _quad.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);   // Quad 法线 +Z → 朝上

        var col = _quad.GetComponent<Collider>();
        if (col != null) Destroy(col);

        _renderer = _quad.GetComponent<Renderer>();
        if (_renderer != null)
        {
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            if (sh != null)
            {
                _material = new Material(sh);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _texture);
                if (_material.HasProperty("_Color")) _material.SetColor("_Color", shadowColor);
                _renderer.sharedMaterial = _material;
            }
        }
    }

    void LateUpdate()
    {
        if (_quad == null) return;

        if (!_radiusResolved) ResolveRadius();

        // 目标中心（狗的身体位置）
        Vector3 center = transform.position;
        if (solver != null && solver.body != null) center = solver.body.position;

        float height = Mathf.Max(0f, center.y - groundY);
        // 淡出高度按模型尺寸自适应，避免"6 单位长的狗"和"0.06 单位长的狗"用同一个阈值
        float fade = Mathf.Max(fadeHeight, _radius * 4f);
        float t = heightInfluence <= 0f ? 0f : Mathf.Clamp01(height / fade);

        // 越高 → 越大、越淡
        float scale = _radius * 2f * (1f + t * heightInfluence);
        // 贴地偏移按半径比例，避免小模型阴影悬空
        float bias = Mathf.Max(0.0002f, _radius * groundBias);
        _quad.transform.position = new Vector3(center.x, groundY + bias, center.z);
        _quad.transform.localScale = new Vector3(scale, scale, 1f);

        if (_material != null && _material.HasProperty("_Color"))
        {
            Color c = shadowColor;
            c.a *= Mathf.Lerp(1f, 0.15f, t);
            _material.SetColor("_Color", c);
        }
    }

    /// <summary>按目标包围球推一个合适的半径，让大小模型都协调</summary>
    void ResolveRadius()
    {
        _radiusResolved = true;

        bool any = false;
        Bounds b = new Bounds(transform.position, Vector3.zero);
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r == null || r == _renderer) continue;
            if (!any) { b = r.bounds; any = true; }
            else { b.Encapsulate(r.bounds.min); b.Encapsulate(r.bounds.max); }
        }

        if (!any) { _radius = 0.5f; return; }

        // 取水平面内半径，避免把"腿竖直向下"的高度也算进去
        float halfX = b.extents.x;
        float halfZ = b.extents.z;
        _radius = Mathf.Max(0.01f, Mathf.Max(halfX, halfZ)) * radiusFactor;
    }

    /// <summary>生成一张中心不透明、边缘透明的径向渐变贴图</summary>
    static Texture2D BuildFalloffTexture(int size, float softness)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name = "CaiyuBlobShadowTex";
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var px = new Color32[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                // d=0 → 1，d>=1 → 0，中间用 softness 控制衰减曲线
                float a = Mathf.Clamp01(1f - Mathf.Pow(d, softness));
                a *= a;   // 让中心更实、边缘更虚

                byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }
}
