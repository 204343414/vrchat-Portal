// ============================================================
// 传送门中文面板（仅编辑器生效，不参与 Udon 编译）
// ============================================================
// 作用：把「双向传送门管理器」和「传送枪」两个 UdonSharpBehaviour 在 Inspector 里显示的
// 字段标签替换成中文，不改动脚本里任何 public 变量名。
//
// 为什么这样做最安全：
// Unity 按“字段名字符串”把 Inspector 里填的数值序列化保存到场景/预制体文件里。
// 如果直接把 C# 代码里的变量名（比如 noClipDepth）改成中文，Unity 会认为这是一个
// “全新的字段”，找不到旧名字对应的存档值，所有你已经调好的数值会被重置为代码里的默认值。
// 这个脚本完全不碰变量名，只在“显示”这一层做文字替换，序列化路径完全不变，
// 因此不存在任何丢失已保存配置的风险，也不会影响 Udon 编译（本文件放在 Editor 文件夹下，
// 打包/上传世界时会被 Unity 自动排除，不会计入 Udon 程序）。
//
// 使用方法：
// 直接放着就行，不需要额外操作。选中挂了这两个脚本的物体，Inspector 会自动显示中文标签。
// 如果以后想加新字段的中文名，只要在下面的字典里加一行 "英文变量名" -> "中文标签" 即可。
// ============================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class 传送门中文面板_标签表
{
    // 双向传送门管理器 字段名 -> 中文标签
    public static readonly Dictionary<string, string> 门管理器标签 = new Dictionary<string, string>
    {
        { "cameraNearClip", "相机近裁剪面" },

        { "portalParentA", "A门 父物体" },
        { "portalPlaneA", "A门 门面" },
        { "cameraA", "A门 摄像机" },
        { "portalMatA", "A门 材质球" },

        { "portalParentB", "B门 父物体" },
        { "portalPlaneB", "B门 门面" },
        { "cameraB", "B门 摄像机" },
        { "portalMatB", "B门 材质球" },

        { "noClipDepth", "门厚度（无碰撞深度）" },
        { "portalTriggerWidth", "门框宽度" },
        { "portalTriggerHeight", "门框高度" },
        { "clipPlaneOffset", "裁剪面偏移" },

        { "portalShapeA", "A门 判定形状(0圆1三角2方框)" },
        { "portalShapeB", "B门 判定形状(0圆1三角2方框)" },
        { "useCircularPortalCheck", "旧版-使用圆形判定" },

        { "portalGun", "传送枪引用" },
        { "colliderDisableBuffer", "碰撞穿透缓冲距离" },
        { "solidCollisionLayer", "实体碰撞层" },
        { "playerPassThroughLayer", "玩家穿透层" },

        { "enableVisibilityOptimization", "启用可见性优化" },
        { "maxRenderDistance", "最大渲染距离" },
        { "maxViewAngle", "最大视角" },
        { "checkInterval", "检测间隔帧数" },

        { "showDebugGizmos", "显示调试线框" },
        { "gizmoColorA", "A门 调试颜色" },
        { "gizmoColorB", "B门 调试颜色" },

        { "vrTargetFOV", "VR目标视场角" },

        { "enableSebRecursiveRendering", "启用递归渲染" },
        { "recursiveRenderLimit", "递归渲染层数上限" },
        { "recursiveForceManualCamerasDisabled", "递归-强制关闭相机自动渲染" },
        { "recursiveUseSkyboxTerminal", "递归-终点用天空盒" },
        { "recursiveHideExitScreen", "递归-隐藏出口门面" },
        { "recursiveEarlyStop", "递归-提前停止" },
        { "recursiveMaxDistance", "递归-提前停止距离" },
        { "recursiveMaxViewAngle", "递归-提前停止视角" },
        { "debugRecursiveRenderLog", "递归-调试日志" },
        { "debugRecursiveLogIntervalFrames", "递归-调试日志间隔帧" },
        { "recursiveDisplayMaskProperty", "递归-显示遮罩属性名" },
        { "recursiveTerminalUseDisplayMask", "递归-终点用显示遮罩" },
        { "recursiveHideExitUseDisplayMask", "递归-隐藏出口用显示遮罩" },
        { "recursiveForceClearSkybox", "递归-强制清屏为天空盒" },
        { "recursiveUseSebObliqueClip", "递归-使用Seb斜切裁剪" },
        { "recursiveNearClipOffset", "递归-近裁剪偏移" },
        { "recursiveNearClipLimit", "递归-近裁剪阈值" },
        { "recursiveForceObliqueClip", "递归-强制斜切裁剪" },
        { "recursiveFlipObliqueClipNormal", "递归-翻转裁剪法线" },
        { "recursiveRenderUseClassicHalfTurn", "递归-使用经典半转" },
        { "recursiveSyncNearClipToPortalPlane", "递归-近裁剪贴合门面" },
        { "recursiveDynamicNearClipPadding", "递归-动态近裁剪余量" },

        { "enablePortalOverlayWhenHeadNear", "近门时门面置顶" },
        { "portalOverlayDepth", "门面置顶触发深度" },
        { "portalOverlayZTestProperty", "门面置顶ZTest属性名" },
        { "recursivePauseDuringTransition", "过渡时暂停递归(旧)" },
        { "recursiveDynamicNearClipMax", "动态近裁剪最大值" },
        { "debugRecursiveClipLog", "递归裁剪调试日志" },

        { "portalViewTransitionCube", "过渡视角立方体" },
        { "transitionDuration", "过渡时长" },
        { "transitionCameraSafeNearClip", "过渡相机安全近裁剪" },

        { "dumpConfigSnapshotOnStart", "开局打印配置快照" },

        { "debugTeleportLog", "调试-总日志开关" },
        { "debugTeleportCoreLog", "调试-核心传送日志" },
        { "debugLayerLog", "调试-图层切换日志" },
        { "debugTransitionLog", "调试-过渡相机日志" },
        { "debugTeleportVerbose", "调试-详细状态日志" },
        { "debugLogIntervalFrames", "调试-日志间隔帧数" },
        { "playerCapsuleRadius", "玩家胶囊体半径" },
        { "playerCapsuleHeight", "玩家胶囊体高度" },
        { "portalSideEpsilon", "门侧判定死区" },
        { "teleportBlockFrames", "传送后屏蔽帧数" },
        { "stopAfterTeleportSameFrame", "传送后结束本帧" },
        { "protectSharedMarkedCollider", "保护共享碰撞体" },

        { "travellerTrackDepth", "追踪触发深度" },
        { "crossingEpsilon", "穿越判定死区" },

        { "teleportTriggerOffset", "传送触发面偏移" },
        { "useRootAsTraveller", "使用根骨追踪" },
        { "useHybridRootXYHeadZTraveller", "混合根骨/头部追踪" },
        { "enableExitSideCorrection", "出口侧保险修正" },
        { "exitSideMinDistance", "出口最小安全距离" },
        { "useVRCTrackingRootTeleport", "旧版-头部反推根骨" },
        { "useScaleFreePortalMatrix", "忽略门物体缩放" },
        { "useClassicHalfTurn", "经典Portal半转" },
        { "keepPlayerUpright", "传送后保持站立" },
        { "enableFlatPortalMomentumSnapping", "地板门动量吸附" },
        { "flatPortalDotThreshold", "地板门判定阈值" },
        { "verticalVelocitySnapThreshold", "垂直速度吸附阈值" },

        { "isVRPlayer", "状态-是否VR玩家" },
        { "currentFOV", "状态-当前视场角" },
        { "isCameraARendering", "状态-A相机渲染中" },
        { "isCameraBRendering", "状态-B相机渲染中" },
        { "isClippingActiveA", "状态-A裁剪生效" },
        { "isClippingActiveB", "状态-B裁剪生效" },
        { "playerNearestPortal", "状态-玩家最近的门" },
        { "portalStateA", "状态-A门状态" },
        { "portalStateB", "状态-B门状态" },
        { "colliderADisabled", "状态-A碰撞体已禁用" },
        { "colliderBDisabled", "状态-B碰撞体已禁用" },
        { "recursiveDepthRenderedA", "状态-A递归渲染深度" },
        { "recursiveDepthRenderedB", "状态-B递归渲染深度" },

        { "velocityReapplyFrames", "速度重发帧数" },
    };

    // 传送枪 字段名 -> 中文标签
    public static readonly Dictionary<string, string> 传送枪标签 = new Dictionary<string, string>
    {
        { "portalA", "传送门A" },
        { "portalB", "传送门B" },

        { "maxDistance", "最大射程" },
        { "shootPoint", "射线发射点" },
        { "placementLayers", "可放置层" },
        { "blockedLayers", "阻挡层" },
        { "wallOffset", "墙面偏移距离" },

        { "cooldownTime", "发射冷却时间" },
        { "playSoundOnCooldown", "冷却中播放失败音效" },

        { "gunAnimator", "枪械动画控制器" },
        { "shootTriggerA", "发射A门Trigger名" },
        { "shootTriggerB", "发射B门Trigger名" },
        { "shootTriggerFail", "发射失败Trigger名" },

        { "audioSource", "音源组件" },
        { "shootSoundsA", "A门音效数组" },
        { "shootSoundsB", "B门音效数组" },
        { "failSounds", "失败音效数组" },

        { "markedColliderA", "A门标记的碰撞体" },
        { "markedColliderB", "B门标记的碰撞体" },

        { "showDebugRay", "显示调试射线" },
        { "rayColorA", "A门射线颜色" },
        { "rayColorB", "B门射线颜色" },
        { "rayColorFail", "失败射线颜色" },

        { "applyBHalfTurnInGun", "旧版-枪身给B门加180度" },
        { "debugPortalGunLog", "调试-放置日志" },
        { "debugPlayerPhysicsOnStart", "调试-开局打印玩家物理参数" },

        { "switchLayerWhenHeld", "手持时切换图层" },
        { "heldLayer", "手持时的图层" },

        { "enablePlacementValidation", "启用合法放置检测" },
        { "portalManager", "传送门管理器引用" },
        { "placementCornerInsetRatio", "贴合检测-角点内缩比例" },
        { "placementGapTolerance", "贴合检测-间隙容差" },
        { "placementMaxIterations", "贴合检测-最大迭代次数" },
        { "placementInitialStep", "贴合检测-初始步长" },
        { "placementMinStep", "贴合检测-最小步长" },
        { "placementMaxTotalCorrection", "贴合检测-累计修正上限" },
        { "placementProbeOutDistance", "贴合检测-探测起点外推距离" },
        { "placementMaxNormalAngle", "贴合检测-最大法线夹角" },
        { "placementObstructionDepth", "遮挡检测-盒子厚度" },
        { "placementObstructionOffset", "遮挡检测-盒子法线偏移" },
        { "placementObstructionLayers", "遮挡检测-检测层" },
        { "enableMutualExclusionCheck", "A/B互斥校验-启用" },
        { "mutualExclusionMargin", "A/B互斥校验-安全间距" },
        { "debugPlacementValidationLog", "调试-放置校验日志" },
    };
}

[CustomEditor(typeof(双向传送门管理器))]
public class 双向传送门管理器_中文面板 : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpEditorGUIHelper.尝试绘制默认头部(serializedObject, target)) return;

        serializedObject.Update();
        UdonSharpEditorGUIHelper.绘制中文字段(serializedObject, 传送门中文面板_标签表.门管理器标签);
        serializedObject.ApplyModifiedProperties();
    }
}

[CustomEditor(typeof(传送枪))]
public class 传送枪_中文面板 : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpEditorGUIHelper.尝试绘制默认头部(serializedObject, target)) return;

        serializedObject.Update();
        UdonSharpEditorGUIHelper.绘制中文字段(serializedObject, 传送门中文面板_标签表.传送枪标签);
        serializedObject.ApplyModifiedProperties();
    }
}

// 通用绘制辅助：按 SerializedObject 的默认可见字段顺序遍历，命中字典就换中文标签，
// 命不中就原样绘制（保留原本的英文名 + Tooltip），保证以后新增字段不会“凭空消失”。
public static class UdonSharpEditorGUIHelper
{
    public static bool 尝试绘制默认头部(SerializedObject so, Object target)
    {
        // 尝试调用 UdonSharpEditor 提供的默认头部（转换成 UdonBehaviour 的按钮、同步设置等）。
        // 用反射调用，避免本文件在没有安装 UdonSharp 编辑器插件时直接编译失败。
        var udonSharpGUIType = FindType("UdonSharpEditor.UdonSharpGUI");
        if (udonSharpGUIType != null)
        {
            var method = udonSharpGUIType.GetMethod("DrawDefaultUdonSharpBehaviourHeader", new System.Type[] { typeof(Object) });
            if (method != null)
            {
                object result = method.Invoke(null, new object[] { target });
                if (result is bool b) return b;
            }
        }
        return false;
    }

    private static System.Type FindType(string fullName)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    public static void 绘制中文字段(SerializedObject so, Dictionary<string, string> 标签表)
    {
        SerializedProperty prop = so.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;

            if (prop.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(prop, true);
                }
                continue;
            }

            string label;
            if (标签表.TryGetValue(prop.name, out label))
            {
                GUIContent content = new GUIContent(label, prop.tooltip);
                EditorGUILayout.PropertyField(prop, content, true);
            }
            else
            {
                EditorGUILayout.PropertyField(prop, true);
            }
        }
    }
}
#endif
