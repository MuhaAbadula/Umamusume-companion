Shader "Umamusume/Eye Surface"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        [HideInInspector] _StencilTest ("Stencil test", Float) = 8
        [HideInInspector] _StencilPass ("Stencil pass", Float) = 2
        [HideInInspector] _DepthTest ("Depth test", Float) = 4
        [HideInInspector] _DepthWrite ("Depth write", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        ZTest [_DepthTest]
        ZWrite [_DepthWrite]
        Stencil
        {
            Ref 1
            ReadMask 1
            WriteMask 1
            Comp [_StencilTest]
            Pass [_StencilPass]
        }
        CGPROGRAM
        #pragma surface surf Lambert alphatest:_Cutoff exclude_path:deferred exclude_path:prepass
        sampler2D _MainTex;
        fixed4 _Color;
        struct Input { float2 uv_MainTex; };
        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 color = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = color.rgb;
            o.Alpha = color.a;
        }
        ENDCG
    }
    Fallback Off
}
