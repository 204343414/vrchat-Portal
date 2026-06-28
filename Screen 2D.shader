Shader "Destruction/Portal_Skybox_TrueSync"
{
    Properties
    {
        _MainTex ("View Texture", 2D) = "white" {}
        
        [Header(Settings)]
        _FOV ("Vertical FOV", Range(1, 179)) = 60 
        _OffsetY ("Vertical Offset", Range(-1, 1)) = 0.0
        
        [Header(Aspect Ratio Source)]
        _TexWidth ("Texture Width", Float) = 1920
        _TexHeight ("Texture Height", Float) = 1080
        
        [Header(______Transition______)]
        _Transition ("BlackHole 0 <---> Normal 1", Range(0, 1)) = 1.0
        
        [Header(______Black Hole Distortion______)]
        _DistortionStrength ("Distortion Strength", Range(0, 8)) = 3.0
        _DistortionRadius ("Effect Radius", Range(0.1, 3)) = 1.2
        _BlackHoleCoreSize ("Core Size", Range(0, 0.5)) = 0.12
        _CoreSoftness ("Core Softness", Range(0.01, 0.3)) = 0.08
        
        [Header(______Edge Outline______)]
        [Toggle] _EnableOutline ("Enable Edge Outline", Float) = 1
        _OutlineWidth ("Outline Width", Range(0.001, 0.1)) = 0.02
        _OutlineColor ("Outline Color Inner", Color) = (1, 0.5, 0.1, 1)
        _OutlineColor2 ("Outline Color Outer", Color) = (1, 0.2, 0.05, 1)
        _OutlineIntensity ("Glow Intensity", Range(0, 10)) = 3.0
        _OutlineSoftness ("Softness", Range(1, 10)) = 3.0
        
        [Header(______Outline Animation______)]
        _FlickerSpeed ("Flicker Speed", Range(0, 10)) = 4.0
        _FlickerAmount ("Flicker Amount", Range(0, 1)) = 0.3
        _PulseSpeed ("Pulse Speed", Range(0, 5)) = 1.5
        _PulseAmount ("Pulse Amount", Range(0, 0.5)) = 0.15
    }
    
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100

        // ============================================================
        // Pass 1: 边缘描边 (轮廓光晕)
        // ============================================================
        Pass
        {
            Name "Outline"
            Cull Front  // 只渲染背面形成轮廓
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha One  // 加法混合让光晕更亮

            CGPROGRAM
            #pragma vertex vert_outline
            #pragma fragment frag_outline
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata_outline
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f_outline
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float expansion : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _EnableOutline;
            float _OutlineWidth;
            float4 _OutlineColor;
            float4 _OutlineColor2;
            float _OutlineIntensity;
            float _OutlineSoftness;
            float _FlickerSpeed;
            float _FlickerAmount;
            float _PulseSpeed;
            float _PulseAmount;
            float _Transition;

            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float noise(float2 p) 
            {
                float2 i = floor(p); 
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(hash(i), hash(i + float2(1,0)), f.x), 
                    lerp(hash(i + float2(0,1)), hash(i + float2(1,1)), f.x), 
                    f.y
                );
            }

            v2f_outline vert_outline(appdata_outline v)
            {
                v2f_outline o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // 脉冲动画影响描边宽度
                float pulse = sin(_Time.y * _PulseSpeed) * _PulseAmount + 1.0;
                float width = _OutlineWidth * pulse;
                
                // 沿法线方向扩展顶点
                float3 expandedVertex = v.vertex.xyz + v.normal * width;
                
                o.pos = UnityObjectToClipPos(float4(expandedVertex, 1.0));
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.viewDir = normalize(_WorldSpaceCameraPos - o.worldPos);
                o.expansion = width / _OutlineWidth; // 记录扩展程度用于渐变
                
                return o;
            }

            fixed4 frag_outline(v2f_outline i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                if(_EnableOutline < 0.5)
                    discard;

                float time = _Time.y;
                
                // 计算边缘因子
                float NdotV = saturate(dot(normalize(i.worldNormal), i.viewDir));
                float edgeFactor = pow(1.0 - NdotV, _OutlineSoftness);
                
                // 火焰闪烁效果
                float angle = atan2(i.worldPos.y, i.worldPos.x);
                float flickerNoise = noise(float2(angle * 3.0 + time * _FlickerSpeed, time * 0.5));
                float flicker = lerp(1.0, flickerNoise, _FlickerAmount);
                
                // 颜色渐变（内到外）
                float colorMix = edgeFactor;
                fixed4 outlineCol = lerp(_OutlineColor, _OutlineColor2, colorMix);
                
                // 最终强度
                float intensity = edgeFactor * _OutlineIntensity * flicker;
                
                // 边缘外部淡出
                float fadeOut = saturate(1.0 - (i.expansion - 1.0) * 5.0);
                intensity *= fadeOut;
                
                fixed4 col = outlineCol * intensity;
                col.a = intensity * 0.8;
                
                return col;
            }
            ENDCG
        }

        // ============================================================
        // Pass 2: GrabPass
        // ============================================================
        GrabPass { "_BackgroundTexture" }

        // ============================================================
        // Pass 3: 主效果 (Portal + 黑洞扭曲)
        // ============================================================
        Pass
        {
            Name "MainEffect"
            ZWrite Off
            ZTest LEqual
            Cull Off 
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing 
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 grabPos : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 objectCenter : TEXCOORD2;
                float3 worldNormal : TEXCOORD3;
                float3 viewDir : TEXCOORD4;
                float camDist : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            sampler2D _BackgroundTexture;
            
            // FOV参数
            float _FOV;
            float _OffsetY;
            float _TexWidth;
            float _TexHeight;
            
            // 过渡
            float _Transition;
            
            // 黑洞参数
            float _DistortionStrength;
            float _DistortionRadius;
            float _BlackHoleCoreSize;
            float _CoreSoftness;

            // 噪声
            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float noise(float2 p) 
            {
                float2 i = floor(p); 
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(hash(i), hash(i + float2(1,0)), f.x), 
                    lerp(hash(i + float2(0,1)), hash(i + float2(1,1)), f.x), 
                    f.y
                );
            }
            float fbm(float2 p) 
            {
                float v = 0.0;
                v += noise(p) * 0.5;
                v += noise(p * 2.0) * 0.25;
                v += noise(p * 4.0) * 0.125;
                return v;
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.grabPos = ComputeGrabScreenPos(o.pos);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.objectCenter = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos - o.worldPos);
                o.camDist = length(_WorldSpaceCameraPos - o.objectCenter);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // ============================================================
                // Portal UV 计算 (使用原始FOV方法)
                // ============================================================
                float3 viewDirWorld = i.worldPos - _WorldSpaceCameraPos;
                float3 viewDirCam = mul((float3x3)UNITY_MATRIX_V, viewDirWorld);
                float2 portalUV = viewDirCam.xy / abs(viewDirCam.z);

                float halfFovRad = _FOV * 0.5 * 0.0174532925;
                float tanHalfFov = tan(halfFovRad);
                float aspect = _TexWidth / max(_TexHeight, 1.0);
                
                portalUV.x /= (tanHalfFov * aspect);
                portalUV.y /= tanHalfFov;
                portalUV *= 0.5;
                portalUV.y += _OffsetY;
                portalUV += 0.5;

                // ============================================================
                // 完全Normal模式 - 直接返回Portal画面
                // ============================================================
                if(_Transition >= 0.999)
                {
                    if(portalUV.x < 0 || portalUV.x > 1 || portalUV.y < 0 || portalUV.y > 1)
                        return fixed4(0,0,0,1);
                    return tex2D(_MainTex, portalUV);
                }

                // ============================================================
                // 黑洞效果计算
                // ============================================================
                float effect = 1.0 - _Transition;
                float time = _Time.y;

                float3 toPixel = i.worldPos - i.objectCenter;
                float3 viewRight = UNITY_MATRIX_V[0].xyz;
                float3 viewUp = UNITY_MATRIX_V[1].xyz;
                float2 projectedOffset = float2(dot(toPixel, viewRight), dot(toPixel, viewUp));
                float worldDist = length(projectedOffset);
                float angle = atan2(projectedOffset.y, projectedOffset.x);

                // ============================================================
                // GrabPass 背景扭曲
                // ============================================================
                float2 grabUV = i.grabPos.xy / i.grabPos.w;
                
                float distortNorm = worldDist / _DistortionRadius;
                float distortPower = _DistortionStrength / (distortNorm * distortNorm + 0.15);
                distortPower = min(distortPower * saturate(1.5 - distortNorm * 0.5), 20.0);
                distortPower *= effect;
                
                float distortNoise = fbm(float2(angle * 2.0, worldDist * 3.0 + time * 0.3));
                float2 distortDir = normalize(projectedOffset + 0.0001);
                
                float2 uvOffset = distortDir * distortPower * (1.0 + distortNoise * 0.4) * (2.0 / max(i.camDist, 0.5)) * 0.01;
                float screenAspect = _ScreenParams.x / _ScreenParams.y;
                uvOffset.x /= screenAspect;
                
                float2 distortedGrabUV = grabUV - uvOffset;
                fixed4 bgColor = tex2D(_BackgroundTexture, distortedGrabUV);

                // ============================================================
                // 黑洞中心
                // ============================================================
                float coreSize = _BlackHoleCoreSize * effect;
                float coreMask = 1.0 - smoothstep(coreSize, coreSize + _CoreSoftness, worldDist);
                float darkenZone = smoothstep(0.0, coreSize + _CoreSoftness * 3.0, worldDist);
                bgColor.rgb *= lerp(darkenZone, 1.0, 1.0 - effect);
                bgColor.rgb = lerp(bgColor.rgb, float3(0,0,0), coreMask);

                // ============================================================
                // Portal画面
                // ============================================================
                fixed4 portalColor = fixed4(0,0,0,1);
                bool inBounds = (portalUV.x >= 0 && portalUV.x <= 1 && portalUV.y >= 0 && portalUV.y <= 1);
                if(inBounds)
                {
                    portalColor = tex2D(_MainTex, portalUV);
                }

                // ============================================================
                // 最终混合
                // ============================================================
                fixed4 finalColor = lerp(bgColor, portalColor, _Transition);
                
                float alphaMask = smoothstep(_DistortionRadius * 1.2, _DistortionRadius * 0.3, worldDist);
                finalColor.a = lerp(alphaMask * effect, 1.0, _Transition);
                
                if(!inBounds && _Transition > 0.5)
                {
                    finalColor = fixed4(0,0,0,1);
                }
                
                return finalColor;
            }
            ENDCG
        }
    }
    
    Fallback "Diffuse"
}