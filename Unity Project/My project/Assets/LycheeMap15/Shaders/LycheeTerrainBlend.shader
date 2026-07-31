Shader "Lychee/Terrain Blend"
{
    Properties
    {
        _GrassTex ("Grass", 2D) = "white" {}
        _DirtTex ("Dirt", 2D) = "white" {}
        _RockTex ("Rock", 2D) = "white" {}
        _GrassTint ("Grass Tint", Color) = (0.62, 0.72, 0.48, 1)
        _LightGrassTint ("Light Grass Tint", Color) = (0.78, 0.76, 0.45, 1)
        _DeepGrassTint ("Deep Grass Tint", Color) = (0.28, 0.42, 0.16, 1)
        _DryGrassTint ("Dry Grass Tint", Color) = (0.62, 0.52, 0.27, 1)
        _DirtTint ("Dirt Tint", Color) = (0.56, 0.46, 0.31, 1)
        _RockTint ("Rock Tint", Color) = (0.52, 0.51, 0.45, 1)
        _TextureScale ("Texture Scale", Float) = 0.22
        _SlopeStart ("Slope Start", Range(0, 1)) = 0.22
        _SlopeEnd ("Slope End", Range(0, 1)) = 0.62
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_GrassTex);
            SAMPLER(sampler_GrassTex);
            TEXTURE2D(_DirtTex);
            SAMPLER(sampler_DirtTex);
            TEXTURE2D(_RockTex);
            SAMPLER(sampler_RockTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _GrassTint;
                float4 _LightGrassTint;
                float4 _DeepGrassTint;
                float4 _DryGrassTint;
                float4 _DirtTint;
                float4 _RockTint;
                float _TextureScale;
                float _SlopeStart;
                float _SlopeEnd;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.shadowCoord = GetShadowCoord(positionInputs);
                return output;
            }

            float SmoothNoise(float2 p)
            {
                float a = sin(p.x * 0.31 + sin(p.y * 0.17));
                float b = sin(p.y * 0.27 + sin(p.x * 0.13));
                float c = sin((p.x + p.y) * 0.09);
                return saturate(0.5 + a * 0.22 + b * 0.18 + c * 0.10);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionWS.xz * _TextureScale;
                float3 normalWS = normalize(input.normalWS);
                float slope = 1.0 - saturate(normalWS.y);
                float rockBlend = smoothstep(_SlopeStart, _SlopeEnd, slope);

                float macroNoise = SmoothNoise(input.positionWS.xz * 0.34 + float2(11.0, 73.0));
                float lowNoise = SmoothNoise(input.positionWS.xz);
                float colorNoise = SmoothNoise(input.positionWS.xz * 0.53 + float2(37.0, 19.0));
                float dryNoise = SmoothNoise(input.positionWS.xz * 0.31 + float2(91.0, 43.0));
                float fineNoise = SmoothNoise(input.positionWS.xz * 2.4 + float2(17.0, 29.0));
                float dirtBlend = smoothstep(0.55, 0.83, lowNoise) * (1.0 - rockBlend) * 0.58;
                dirtBlend += smoothstep(0.72, 0.91, macroNoise) * (1.0 - rockBlend) * 0.18;

                float3 grassBase = SAMPLE_TEXTURE2D(_GrassTex, sampler_GrassTex, uv).rgb;
                float3 grassTint = lerp(_DeepGrassTint.rgb, _GrassTint.rgb, smoothstep(0.18, 0.56, colorNoise));
                grassTint = lerp(grassTint, _LightGrassTint.rgb, smoothstep(0.58, 0.86, colorNoise));
                grassTint = lerp(grassTint, _DryGrassTint.rgb, smoothstep(0.72, 0.94, dryNoise) * 0.72);
                grassTint *= lerp(0.84, 1.14, macroNoise);
                float3 grass = grassBase * grassTint * lerp(0.92, 1.08, fineNoise);
                float3 dirt = SAMPLE_TEXTURE2D(_DirtTex, sampler_DirtTex, uv * 0.82).rgb * _DirtTint.rgb;
                float3 rock = SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, uv * 0.70).rgb * _RockTint.rgb;
                float3 albedo = lerp(grass, dirt, dirtBlend);
                albedo = lerp(albedo, rock, rockBlend);

                Light mainLight = GetMainLight(input.shadowCoord);
                float diffuse = saturate(dot(normalWS, mainLight.direction));
                float3 lighting = SampleSH(normalWS) * 0.78 + mainLight.color * diffuse * mainLight.shadowAttenuation;
                return half4(albedo * lighting, 1.0);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }
}
