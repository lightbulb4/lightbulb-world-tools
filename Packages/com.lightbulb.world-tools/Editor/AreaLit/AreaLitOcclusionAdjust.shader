Shader "Hidden/Lightbulb/AreaLitOcclusionAdjust"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Brightness ("Brightness", Float) = 1
        _Contrast ("Contrast", Float) = 1
        _EncodeForDisplay ("Encode For Display", Float) = 0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Brightness;
            float _Contrast;
            float _EncodeForDisplay;

            float4 frag(v2f_img input) : SV_Target
            {
                float4 source = tex2D(_MainTex, input.uv);
                float3 adjusted = ((source.rgb * _Brightness) - 0.5) * _Contrast + 0.5;
                // An exact zero often means this pixel belongs to a different packed RGB channel.
                // Keep it zero so lowering contrast cannot leak one AreaLit channel into another.
                adjusted *= step(0.000001, source.rgb);
                // Visibility must not amplify AreaLit's emitter opacity above its intended range.
                adjusted = saturate(adjusted);

                // EditorGUI.DrawPreviewTexture needs display encoding in a linear-color-space
                // project. Saved HDR data must remain linear, so file output leaves this disabled.
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    if (_EncodeForDisplay > 0.5)
                    {
                        adjusted = LinearToGammaSpace(adjusted);
                    }
                #endif

                return float4(adjusted, source.a);
            }
            ENDCG
        }
    }
}
