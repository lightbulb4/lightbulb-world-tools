Shader "Hidden/Lightbulb/ReadMaterialMap"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            int _Row;
            float4 frag(v2f_img input) : SV_Target
            {
                // Integer mip-zero loads: no filtering, resizing, normal decoding, or loss of packed alpha.
                return _MainTex.Load(int3(int2(input.pos.xy) + int2(0, _Row), 0));
            }
            ENDHLSL
        }
    }
}
