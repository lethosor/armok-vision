﻿Shader "Custom/ArmokStandardPBRCutout" {
	Properties {
		_Color ("Color", Color) = (1,1,1,1)
		_MainTex ("Albedo (RGB)", 2D) = "grey" {}
		_BumpMap ("Normalmap (RGB) Occlusion (A)", 2D) = "bump" {}

		_Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
	}
	SubShader {
		Tags { "Queue"="AlphaTest" "IgnoreProjector"="True" "RenderType"="TransparentCutout" }
		Tags { "RenderType"="Opaque" }
		LOD 200
		Cull Off
		
		CGPROGRAM
		// Physically based Standard lighting model, and enable shadows on all light types
		#pragma surface surf Standard addshadow alphatest:_Cutoff

		// Use shader model 3.0 target, to get nicer looking lighting
		#pragma target 3.0

		sampler2D _MainTex;
		sampler2D _Shapetex;
		sampler2D _BumpMap;

		struct Input {
			float2 uv_MainTex;
			float2 uv2_BumpMap;
			float4 color: Color; // Vertex color
		};

		half _Glossiness;
		fixed4 _Color;

		void surf (Input IN, inout SurfaceOutputStandard o) {
			// Albedo comes from a texture tinted by color
			fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
			fixed4 b = tex2D (_BumpMap, IN.uv2_BumpMap);
			//o.Albedo = c.rgb * IN.color.rgb;
			o.Albedo = c.rgb < 0.5 ? (2.0 * c.rgb * IN.color.rgb) : (1.0 - 2.0 * (1.0 - c.rgb) * (1.0 - IN.color.rgb));
			o.Metallic = 1.0 - IN.color.a;
			o.Smoothness = b.b;
			o.Occlusion = b.a;
			o.Normal = b.rgb;
			o.Alpha = c.a;
		}
		ENDCG
	} 
	FallBack "Diffuse"
}
