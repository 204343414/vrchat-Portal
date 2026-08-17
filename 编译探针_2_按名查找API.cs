// ════════════════════════════════════════════════════════════════════
// ⚠️ 一次性编译探针 2 —— 验证 GameObject.Find 是否在 UdonSharp 白名单内
// ════════════════════════════════════════════════════════════════════
// 目的：全场景枚举类 API（FindObjectsOfType / Scene.GetRootGameObjects）已被
//       Udon 沙箱禁用；如果 GameObject.Find（按名字找单个物体）可用，就能用
//       "命名约定"发现没有碰撞体的特效物体（OverlapSphere 爬不到的那类）。
//
// 用法：
//   1. 把本文件放进 Unity 工程 Assets 下（⚠️ 不要放进 Editor 文件夹）；
//   2. 场景里随便放一个物体，命名改为 PortalProbeMarker（可以禁用显示，但
//      ⚠️ 物体必须是激活状态——Unity 的 Find 找不到非激活物体）；
//   3. 随便建一个空 GameObject，挂上这个脚本；
//   4. 看 Console：
//        - 报错 → GameObject.Find 不在白名单，此路不通；
//        - 无报错 + 运行后打印 [编译探针2] find=PortalProbeMarker → 可用；
//   5. 测完务必删除本文件，并汇报结果。
// ════════════════════════════════════════════════════════════════════
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 编译探针_2_按名查找API : UdonSharpBehaviour
{
    void Start()
    {
        GameObject found = GameObject.Find("PortalProbeMarker"); // ← 探测行
        Debug.Log("[编译探针2] find=" + (found != null ? found.name : "null"));
    }
}
