// 车队头顶事件信标专用 shader（冰鉴/快马/荔枝转坏的 3D 模型）。
// 设计目标（见 Docs/交接_事件特效3D模型_20260629.md 决策5）：
//   1) 永不被挡：ZTest Always + 运行时高 renderQueue，盖过车厢/其它车队/地形，像个常亮信标。
//   2) 保留贴图 + 3D 体积感：采样 GLB 自带的 baseColor 贴图（运行时由 LycheeConvoyVfx 转绑到 _BaseMap），
//      用顶点法线做平滑着色 + 配色 tint（调淡让贴图本色显出来）+ 边缘 rim 微光。
//   3) WebGL 安全：只 include Core.hlsl、不依赖 Lighting/阴影关键字，绝不变洋红。
Shader "Lychee/Overhead Event Model"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _EmissionColor("Emission", Color) = (0, 0, 0, 1)
        _ShadeFloor("Shade Floor", Range(0, 1)) = 0.55
        _RimPower("Rim Power", Range(0.5, 6)) = 2.6
        _RimStrength("Rim Strength", Range(0, 2)) = 0.5
    }

    SubShader
    {
        // Queue 走 Geometry，运行时再把 material.renderQueue 抬到 ~3100，让信标画在场景透明特效之后；
        // ZTest Always 已经保证盖过所有不透明几何，queue 只决定与透明光柱/光环的先后。
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "OverlayForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest Always

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _EmissionColor;
                float _ShadeFloor;
                float _RimPower;
                float _RimStrength;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                // 烘死的光照方向（不依赖场景灯，任何机位/任何图都同样读出体积）。
                float3 lightDir = normalize(float3(0.35, 0.85, 0.4));
                float ndl = saturate(dot(normalWS, lightDir));
                float shade = _ShadeFloor + (1.0 - _ShadeFloor) * ndl;

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb;   // 贴图本色 × 调淡的配色 tint

                // 边缘 rim：信标轮廓泛配色光，远机位也跳得出来。
                float rim = pow(1.0 - saturate(dot(normalWS, viewDir)), _RimPower) * _RimStrength;

                half3 color = albedo * shade + _EmissionColor.rgb + _BaseColor.rgb * rim;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
