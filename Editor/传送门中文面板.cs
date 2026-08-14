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
// ================================================================================
// 交接文档 —— 写给下一个接手这份代码的人（人类或 LLM）
// 最后更新：本轮对话结束时（传送枪合法放置检测 + 三形状判定 + 冷却消抖 之后）
// ================================================================================
//
// 【项目结构，一共3个脚本】
//   双向传送门管理器.cs（本文件）：门的渲染（Seb风格递归渲染+oblique裁剪）、传送判定与坐标映射、
//                                过渡视角、碰撞穿透（Layer切换）。这是最核心也最庞大的脚本(2700+行)。
//   传送枪.cs：拾取物，负责把 portalA/portalB 这两个 Transform 挪到玩家瞄准的位置，
//              包含合法放置检测（贴合校验+遮挡校验）、手持图层切换、冷却/输入消抖。
//   Editor/传送门中文面板.cs：只在编辑器生效的自定义 Inspector，把上面两个脚本的英文字段名
//              显示成中文标签，不改变量名本身，不影响 Udon 编译，也不会导致已保存的场景数值丢失。
//
// 【几个绝对不能想当然去改的核心约定，改错了会导致传送方向/朝向全错】
//
// 1. 经典 Portal 半转 (useClassicHalfTurn，默认 true，必须保持 true)：
//    传送枪放置B门时，B门本体的 Transform 不再被额外转180度（传送枪那边 applyBHalfTurnInGun 必须是 false）。
//    所有"A门→B门"的坐标/朝向/速度映射，统一在这个管理器脚本内部用 LocalHalfTurn()（Y轴180度旋转）
//    去乘一次，映射公式统一形如 to * halfTurn * from^-1。递归渲染、摄像机镜像、传送坐标变换、
//    过渡视角旋转补偿——全部必须用同一个 halfTurn，绝对不能有的地方转、有的地方不转，
//    否则地板/天花板门会立刻出现"传送后立刻被传送回去"的鬼畜循环。
//    如果以后想改这个约定，必须搜索全文件所有 `useClassicHalfTurn` 和 `LocalHalfTurn()` 的调用点，
//    一次性全部改掉，不能只改一处。
//
// 2. 传送门形状系统 (portalShapeA / portalShapeB，int 类型，0=圆形 1=三角形 2=方框)：
//    - 用 int 常量（PORTAL_SHAPE_CIRCLE/TRIANGLE/BOX/UNSET）而不是 C# enum，是刻意的：
//      UdonSharp 对自定义 enum 有一些已知的默认值/相等比较的坑，为了稳妥直接用 int。
//    - 默认值是 PORTAL_SHAPE_UNSET(-1)，代表"没有手动设置"，此时会自动回退到旧字段
//      useCircularPortalCheck 换算（true→圆形，false→方框）。这是为了兼容旧场景/旧Prefab，
//      不要把默认值改成 0，否则旧场景升级后行为会静默突变。
//    - 唯一负责判定的函数是 LocalPointInPortalRect(localPoint, shapeType)，
//      所有用到"点是否在门范围内"的地方（传送触发判定、碰撞穿透区域、可见性/体积判定、
//      Gizmos调试线框）全部调用这一个函数、传入 ResolvePortalShape(isPortalA) 解析出来的实际形状。
//      如果以后要加第4种形状，只需要改这一个函数 + GetPortalShapeOutline2D（Gizmos可视化用），
//      不需要动其他任何调用点。
//    - 三角形定义：等腰三角形，尖角朝上，底边在下，用符号面积法判定点在不在三角形内（无三角函数）。
//    - 传送枪那边的"正面遮挡校验"是个例外：无论门是什么形状，永远用矩形包围盒判定，
//      这是刻意的简化（矩形能放，圆形/三角形作为矩形的内切/内接子集必然也能放），不是遗漏。
//
// 3. Traveller 追踪模式 (useRootAsTraveller=true, useHybridRootXYHeadZTraveller=true，
//    这是当前测试出来手感最好的组合，不建议改动)：
//    判断"玩家有没有穿过传送门"这件事，横向XY用玩家根骨(root)位置，纵深Z用头部(head)位置，
//    这样歪头不会误触发传送，但地板/天花板门需要头部真正穿过深度才会触发，不会因为脚先落地
//    就卡在天花板。TeleportSebStyle() 里对"地板门"和"墙面门"两种出口分别有不同的 root/head
//    换算公式（flatHybridTraveller 分支），这部分逻辑很绕，改之前务必在测试世界里对着地板门/
//    墙面门/斜面门分别实测，不要只测一种朝向就断言"改对了"。
//
// 4. 传送触发使用独立的触发平面 (teleportTriggerOffset，默认跟随 noClipDepth)，
//    不是在门的正中心(z=0)触发，而是在门框外侧 z=±teleportTriggerOffset 处触发。
//    这是为了让"从A门外侧穿入→在B门外侧穿出"的沉浸感更连贯（穿过整个门厚度才算数），
//    如果改小/改到0会退化成旧版"沾到门中心线就传送"的行为，手感会变差但不会报错。
//
// 5. Layer 穿透方案 (solidCollisionLayer=28 → playerPassThroughLayer=25)：
//    玩家靠近门时，传送枪打中的碰撞体所在物体会被临时切到 25 层（需要在 Unity 的
//    Physics Collision Matrix 里把 25 设置成"不与 Player 碰撞，但仍与其他刚体碰撞"）。
//    这是替代旧版"直接禁用 Collider"的方案，好处是刚体/物品依然能撞到墙。
//    如果 A、B 两扇门恰好打在同一个 Collider 上，有 protectSharedMarkedCollider 兜底
//    防止两扇门互相抢着复原 Layer，不要在没搞懂这段共享保护逻辑之前就删掉它。
//
// 【传送枪那边这次新加的东西，如果要继续往上迭代，从这几个方法看起】
//   ValidateAndCorrectPlacement()：四角贴合校验，二分搜索式步长收敛（不是固定步长，也不是
//     一次性解析解），已经用独立的数值模拟脚本验证过多种"十字穿模墙面"场景的收敛性和方向正确性，
//     核心细节：贴合目标间隙是 wallOffset 而不是 0；纠偏方向看"哪一侧偏差(badness)更大"而不是
//     直接对"有符号间隙"做差（后者在"悬空"和"嵌入"两种物理状态下方向会算反，这是本轮踩过的坑，
//     写在这里防止未来重蹈覆辙）。
//   CheckFrontObstruction()：正面遮挡校验，矩形 OverlapBox，只排除贴合用的墙面自己和
//     "当前正在放置的这一扇门"自身(selfPortal参数)，绝不排除另一扇门。
//   TryShootPortal() 开头的 RaycastAll 逻辑：Portal 原作手感，瞄准射线只会穿过"自己"这一扇门
//     (portal = isPortalA ? portalA : portalB)自身的碰撞体（含子物体），这样贴着自己的门也能
//     重新微调位置；但另一扇门必须保留为正常障碍物，射线/遮挡检测都不能把它一起忽略掉——
//     早期版本曾经把portalA、portalB两个都从过滤名单里排除，导致瞄B门时激光直接穿过B命中
//     它背后的墙，于是"A被放到了B的位置上"，这是本轮修复的一个真实回归bug，写在这里防止
//     未来重构时不小心又把两扇门都塞进忽略名单。
//   CheckMutualExclusion()：独立于上面两项的第三道防线，不依赖任何碰撞体/图层配置，
//     纯粹用两扇门的包围球半径之和判断候选位置离对方是否太近。这是"A只能放A自己身上、
//     B只能放B自己身上，A放到B身上或反之永远不合法"这条基本规则的硬性兜底——哪怕以后
//     场景里的门框忘了挂碰撞体、或者碰撞体设成了Trigger，这道检查依然能生效。
//     调用点用 enableMutualExclusionCheck 独立开关（不挂在 enablePlacementValidation 下面），
//     因为这是正确性问题不是可选的美观校验，即使用户关掉贴合/遮挡校验也不该被一起关掉。
//   Update() 里的 scrollUpLatched/scrollDownLatched：鼠标滚轮是连续轴不是按键，必须做边缘锁存，
//     否则冷却期间/冷却刚结束瞬间同一次滚动手势会被识别成好几次开火，反复触发失败音效。
//     VR 扳机那边同理已经有 vrTriggerLeftPressed/vrTriggerRightPressed 锁存，是同一个道理。
//
// 【刚体传送 + 抓取已完成（2026-07-09）】
//   - 传送枪新增：Kinematic 抓取（鼠标左键 / VR Grip）、临时质量=1、Layer 切换、grabSounds、Animator bool 可配置。
//   - 管理器新增：OverlapBox + LocalPointInPortalRect(shape) 检测、prevPos 穿越判断、复用 TeleportSebStyle/halfTurn 逻辑。
//   - 支持：抓取中物体可无缝跨门（UpdateHeldAfterTeleport）、Layer 自动切 25、速度/位置/旋转 1:1 映射。
//   - 视觉：完整传送（相对坐标保持），无需新 Shader。性能优先（复用 checkInterval）。
//   - 泛用性：可直接做 Prefab，无需新组件。networking 留空（本地优先）。
//   - 交接注意：刚体传送完全复用现有 traveller/halfTurn/形状系统，极高鲁棒性。
// ================================================================================
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
