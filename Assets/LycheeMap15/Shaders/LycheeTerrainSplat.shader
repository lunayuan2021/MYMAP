Shader "Lychee/Terrain Splat"
{
    // 地形四层 splat 混合（20260613）：草地单贴图平铺重复感强，改为顶点色权重的多层混合。
    // 权重约定（由 LycheeTerrainSplat_20260613 编辑器脚本烘进顶点色）：
    //   R = 层1 路缘土、G = 层2 岩/砂、B = 层3 变化草、底层主草权重 = 1-R-G-B（烘焙端保证非负）、
    //   A = 低频噪声，供主草 texture bombing 简化版打破平铺重复。
    // UV 用世界 XZ 投影乘各层 tiling：生成地形网格的原 UV 不可靠，且与旧 Lychee/Terrain Blend 观感一致。
    Properties
    {
        _Layer0 ("Layer0 主草", 2D) = "white" {}
        _Layer1 ("Layer1 路缘土", 2D) = "white" {}
        _Layer2 ("Layer2 岩/砂", 2D) = "white" {}
        _Layer3 ("Layer3 变化草", 2D) = "white" {}
        _Tiling0 ("Layer0 Tiling (每世界米重复数)", Float) = 0.125
        _Tiling0B ("Layer0 副采样 Tiling (破重复)", Float) = 0.039
        _Tiling1 ("Layer1 Tiling", Float) = 0.078
        _Tiling2 ("Layer2 Tiling", Float) = 0.0625
        _Tiling3 ("Layer3 Tiling", Float) = 0.159
        _Tint0 ("Layer0 Tint", Color) = (1, 1, 1, 1)
        _Tint1 ("Layer1 Tint", Color) = (1, 1, 1, 1)
        _Tint2 ("Layer2 Tint", Color) = (1, 1, 1, 1)
        _Tint3 ("Layer3 Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 关键字最小化：只保留主光阴影（含级联）与雾，控制 WebGL 变体数量
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Layer0); SAMPLER(sampler_Layer0);
            TEXTURE2D(_Layer1); SAMPLER(sampler_Layer1);
            TEXTURE2D(_Layer2); SAMPLER(sampler_Layer2);
            TEXTURE2D(_Layer3); SAMPLER(sampler_Layer3);

            CBUFFER_START(UnityPerMaterial)
                float _Tiling0;
                float _Tiling0B;
                float _Tiling1;
                float _Tiling2;
                float _Tiling3;
                half4 _Tint0;
                half4 _Tint1;
                half4 _Tint2;
                half4 _Tint3;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                half4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                output.color = input.color;
                return output;
            }

            // 低频 sin 伪噪声：叠在烘焙的顶点色 A 上，避免顶点级噪声在大三角形上插值过于平缓
            half SinNoise(float2 p)
            {
                half a = sin(p.x * 0.171 + sin(p.y * 0.131) * 1.7);
                half b = sin(p.y * 0.113 + sin(p.x * 0.097) * 1.3);
                return saturate(0.5 + a * 0.27 + b * 0.23);
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 三平面映射：原来只用世界 XZ 的平面投影 UV，在陡壁上会把贴图竖向拉成条纹（用户看到的"荒草"）。
                // 改为按世界法线混合 zy/xz/xy 三向投影 → 平地仍走 xz（不变），陡壁改走侧投影、不再拉花。
                float3 wpos = input.positionWS;
                float3 wn = normalize(input.normalWS);
                half3 tw = abs(wn); tw = tw / (tw.x + tw.y + tw.z + 0.0001);

                // 顶点色权重：R=土 G=岩砂 B=变化草，底层主草 = 1-R-G-B
                half wDirt = input.color.r;
                half wRock = input.color.g;
                half wVar  = input.color.b;
                half wBase = saturate(1.0 - wDirt - wRock - wVar);

                #define TRI3(tex, smp, tl) ( \
                    SAMPLE_TEXTURE2D(tex, smp, wpos.zy * (tl)).rgb * tw.x + \
                    SAMPLE_TEXTURE2D(tex, smp, wpos.xz * (tl)).rgb * tw.y + \
                    SAMPLE_TEXTURE2D(tex, smp, wpos.xy * (tl)).rgb * tw.z )

                // 主草 texture bombing：同一张草贴图取两个 tiling 按低频噪声插值，打散平铺格子感
                half bomb = saturate(input.color.a * 0.72 + SinNoise(wpos.xz) * 0.28);
                bomb = smoothstep(0.32, 0.68, bomb);
                half3 grassNear = TRI3(_Layer0, sampler_Layer0, _Tiling0);
                half3 grassFar  = TRI3(_Layer0, sampler_Layer0, _Tiling0B);
                half3 layer0 = lerp(grassNear, grassFar, bomb) * _Tint0.rgb;

                half3 layer1 = TRI3(_Layer1, sampler_Layer1, _Tiling1) * _Tint1.rgb;
                half3 layer2 = TRI3(_Layer2, sampler_Layer2, _Tiling2) * _Tint2.rgb;
                half3 layer3 = TRI3(_Layer3, sampler_Layer3, _Tiling3) * _Tint3.rgb;
                #undef TRI3

                half3 albedo = layer0 * wBase + layer1 * wDirt + layer2 * wRock + layer3 * wVar;

                // 光照从简但正确：Lambert 主光（带阴影衰减）+ 环境 SH。
                // 地形以俯视角为主，法线贴图/高光收益低，省下 WebGL 开销
                float3 normalWS = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS) + mainLight.color * (ndl * mainLight.shadowAttenuation);
                half3 color = albedo * lighting;
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
        // 刻意不加 ShadowCaster pass：地形只接收阴影、不需要向别处投影（自身起伏平缓），省一遍绘制
    }
}
