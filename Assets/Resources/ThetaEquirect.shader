// 内向き球体に equirectangular 映像を貼るための Unlit シェーダ。
// Single Pass Instanced (VR) 対応。_FlipU で左右反転(鏡像)を補正できる。
Shader "Theta/Equirect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        [Toggle] _FlipU ("Flip Horizontal", Float) = 0
        [Toggle] _FlipV ("Flip Vertical", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Background" }
        Cull Off
        ZWrite On
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _FlipU;
            float _FlipV;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipU > 0.5)
                    uv.x = 1.0 - uv.x;
                if (_FlipV > 0.5)
                    uv.y = 1.0 - uv.y;
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
}
