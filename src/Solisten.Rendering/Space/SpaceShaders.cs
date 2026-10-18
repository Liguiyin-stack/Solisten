using ComputeSharp;
using ComputeSharp.D2D1;

namespace Solisten.Rendering.Space;

/// <summary>着色器共用的噪声函数。</summary>
internal static class ShaderNoise
{
    public static float Hash(float2 p)
    {
        p = Hlsl.Frac(p * new float2(123.34f, 456.21f));
        p += Hlsl.Dot(p, p + 45.32f);
        return Hlsl.Frac(p.X * p.Y);
    }

    public static float Hash3(float3 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new float3(12.9898f, 78.233f, 37.719f))) * 43758.5453f);

    public static float Noise(float2 p)
    {
        float2 i = Hlsl.Floor(p);
        float2 f = Hlsl.Frac(p);
        float2 u = f * f * (3f - 2f * f);
        float a = Hash(i);
        float b = Hash(i + new float2(1, 0));
        float c = Hash(i + new float2(0, 1));
        float d = Hash(i + new float2(1, 1));
        return Hlsl.Lerp(Hlsl.Lerp(a, b, u.X), Hlsl.Lerp(c, d, u.X), u.Y);
    }

    public static float Fbm(float2 p)
    {
        float v = 0;
        float a = 0.5f;
        for (int i = 0; i < 4; i++)
        {
            v += a * Noise(p);
            p = p * 2.02f + 17.3f;
            a *= 0.5f;
        }
        return v;
    }
}

/// <summary>
/// 薄雾与光晕：在背景（模糊封面）之上叠加由封面色样上色的流动雾气和中央光晕。
/// 浓度随低频呼吸，扭曲随中频，流向和速度由空间气质决定。所有参数为 0 时只剩静止的淡雾。
/// </summary>
[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct NebulaShader : ID2D1PixelShader
{
    private readonly float time;
    private readonly float2 size;
    private readonly float2 center;
    private readonly float radius;
    private readonly float breath;
    private readonly float bend;
    private readonly float pulse;
    private readonly float2 flow;
    private readonly float2 parallax;
    private readonly float fog;
    private readonly float3 c0;
    private readonly float3 c1;
    private readonly float3 c2;
    private readonly float3 glow;

    public NebulaShader(float time, float2 size, float2 center, float radius, float breath, float bend, float pulse,
        float2 flow, float2 parallax, float fog, float3 c0, float3 c1, float3 c2, float3 glow)
    {
        this.time = time;
        this.size = size;
        this.center = center;
        this.radius = radius;
        this.breath = breath;
        this.bend = bend;
        this.pulse = pulse;
        this.flow = flow;
        this.parallax = parallax;
        this.fog = fog;
        this.c0 = c0;
        this.c1 = c1;
        this.c2 = c2;
        this.glow = glow;
    }

    public float4 Execute()
    {
        float2 pos = D2D.GetScenePosition().XY;
        float2 uv = pos / size.Y;
        float2 p = uv * 2.2f + parallax * 9f + flow * time;

        // 域扭曲：中频越强，雾越弯曲流动
        float2 warp = new float2(ShaderNoise.Fbm(p * 0.7f + time * 0.03f), ShaderNoise.Fbm(p * 0.7f - time * 0.025f + 5.2f));
        float n = ShaderNoise.Fbm(p + warp * (0.6f + bend * 0.9f));
        float n2 = ShaderNoise.Fbm(p * 1.7f + 3.1f);

        float3 tint = Hlsl.Lerp(c0, c1, Hlsl.Saturate(n * 1.4f - 0.2f));
        tint = Hlsl.Lerp(tint, c2, Hlsl.Saturate(n2 - 0.35f) * 0.8f);

        float2 centered = (pos - size * 0.5f) / size.Y;
        float vignette = 1f - Hlsl.SmoothStep(0.25f, 1.05f, Hlsl.Length(centered));
        float density = (0.16f + 0.30f * breath + 0.22f * pulse) * fog * Hlsl.SmoothStep(0.28f, 0.85f, n) * vignette;

        float d = Hlsl.Length(pos - center) / radius;
        float halo = Hlsl.Exp(-d * d * 0.9f) * (0.10f + 0.22f * breath + 0.38f * pulse);

        float3 bg = D2D.GetInput(0).XYZ * (0.94f + 0.08f * breath);
        float3 col = bg + tint * density + glow * halo;
        return new float4(col, 1f);
    }
}

