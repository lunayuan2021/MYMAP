Shader "Lychee/River Water"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.08, 0.46, 0.62, 0.82)
        _ShallowColor("Shallow Color", Color) = (0.22, 0.70, 0.72, 0.72)
        _NormalMap("Flow Normal", 2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 0.65
        _FlowSpeed("Flow Speed", Range(0, 1)) = 0.12
        _Smoothness("Smoothness", Range(0, 1)) = 0.86
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShallowColor;
                float4 _NormalMap_ST;
                float _NormalStrength;
                float _FlowSpeed;
                float _Smoothness;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _NormalMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uvA = input.uv + float2(0.0, _Time.y * _FlowSpeed);
                float2 uvB = input.uv * 1.7 + float2(0.08, -_Time.y * _FlowSpeed * 0.63);
                float3 normalA = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvA));
                float3 normalB = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvB));
                float3 normalWS = normalize(input.normalWS + float3(normalA.x + normalB.x, 0, normalA.y + normalB.y) * _NormalStrength);

                Light mainLight = GetMainLight();
                float diffuse = saturate(dot(normalWS, mainLight.direction)) * 0.24 + 0.76;
                float fresnel = pow(1.0 - saturate(dot(normalWS, GetWorldSpaceNormalizeViewDir(input.positionWS))), 3.0);
                float ripple = saturate((normalA.x + normalB.y) * 0.45 + 0.5);
                float3 color = lerp(_BaseColor.rgb, _ShallowColor.rgb, ripple * 0.34 + fresnel * 0.42);
                color *= diffuse;
                color += mainLight.color * pow(saturate(dot(reflect(-mainLight.direction, normalWS), GetWorldSpaceNormalizeViewDir(input.positionWS))), 18.0) * _Smoothness * 0.35;
                return half4(color, lerp(_BaseColor.a, _ShallowColor.a, fresnel));
            }
            ENDHLSL
        }
    }
}
