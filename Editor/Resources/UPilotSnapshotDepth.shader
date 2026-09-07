Shader "Hidden/UPilot/SnapshotDepth"
{
    Properties
    {
        _UPilotDepthTexture ("Depth", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragRaw
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_UPilotDepthTexture);

            float fragRaw(v2f_img input) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_UPilotDepthTexture, input.uv);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragLinear
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_UPilotDepthTexture);

            float fragLinear(v2f_img input) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_UPilotDepthTexture, input.uv);
                return LinearEyeDepth(rawDepth);
            }
            ENDCG
        }

        // RenderGraphUtils draws a procedural full-screen triangle. Keep
        // separate passes so Built-in Graphics.Blit can retain vert_img.
        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProcedural
            #pragma fragment fragRawProcedural
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_UPilotDepthTexture);

            struct ProceduralVaryings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            ProceduralVaryings vertProcedural(uint vertexId : SV_VertexID)
            {
                ProceduralVaryings output;
                output.uv = float2((vertexId << 1) & 2, vertexId & 2);
                output.vertex = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
                return output;
            }

            float fragRawProcedural(ProceduralVaryings input) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_UPilotDepthTexture, input.uv);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProcedural
            #pragma fragment fragLinearProcedural
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_UPilotDepthTexture);

            struct ProceduralVaryings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            ProceduralVaryings vertProcedural(uint vertexId : SV_VertexID)
            {
                ProceduralVaryings output;
                output.uv = float2((vertexId << 1) & 2, vertexId & 2);
                output.vertex = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
                return output;
            }

            float fragLinearProcedural(ProceduralVaryings input) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_UPilotDepthTexture, input.uv);
                return LinearEyeDepth(rawDepth);
            }
            ENDCG
        }
    }
    Fallback Off
}
