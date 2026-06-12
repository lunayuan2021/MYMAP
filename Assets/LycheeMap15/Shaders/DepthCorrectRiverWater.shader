Shader "LycheeMap/DepthCorrectRiverWater"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.16, 0.42, 0.62, 0.68)
        _ShallowColor ("Shallow Highlight", Color) = (0.24, 0.62, 0.72, 1.0)
        _WaveScale ("Wave Scale", Float) = 1.8
        _WaveSpeed ("Wave Speed", Float) = 0.55
        _WaveStrength ("Wave Strength", Range(0, 0.25)) = 0.07
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half fogFactor : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _ShallowColor;
                float _WaveScale;
                float _WaveSpeed;
                float _WaveStrength;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float phaseA = (input.positionWS.x + input.positionWS.z) * _WaveScale;
                float phaseB = (input.positionWS.x - input.positionWS.z) * (_WaveScale * 0.63);
                half wave = (sin(phaseA + _Time.y * _WaveSpeed) +
                             sin(phaseB - _Time.y * _WaveSpeed * 0.73)) * 0.25h + 0.5h;
                half3 color = lerp(_BaseColor.rgb, _ShallowColor.rgb, wave * _WaveStrength);
                color = MixFog(color, input.fogFactor);
                return half4(color, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
