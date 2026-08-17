// ════════════════════════════════════════════════════════════════════
// ⚠️ 一次性编译探针 1 —— 验证 ps.main.simulationSpace 是否在 UdonSharp 白名单内
// ════════════════════════════════════════════════════════════════════
// 目的：Local/World 粒子自动检测需要读 ParticleSystem.main.simulationSpace。
//       上一任开发者因怀疑该 API 不在 Udon 白名单而绕道手动清单；本轮决定实测求证。
//
// 用法：
//   1. 把本文件放进 Unity 工程 Assets 下（⚠️ 不要放进 Editor 文件夹）；
//   2. 随便建一个空 GameObject，挂上这个脚本（自动生成 UdonBehaviour）；
//   3. 把任意一个 ParticleSystem 拖进 target 槽；
//   4. 看 Console 的 Udon 编译报错：
//        - A 行报错 → ParticleSystem.main 结构体访问不被支持；
//        - B 行报错 → simulationSpace 属性（或枚举转 int）不被支持；
//        - 无报错 + 运行后打印 [编译探针1] → 两条都可用，自动检测可行；
//   5. 保险起见可用 VRChat SDK 的 Build & Test 再确认一次；
//   6. 测完务必删除本文件，并汇报：报错行号 / 或打印结果。
// ════════════════════════════════════════════════════════════════════
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 编译探针_1_粒子主模块API : UdonSharpBehaviour
{
    public ParticleSystem target;

    void Start()
    {
        if (target == null) return;
        ParticleSystem.MainModule mm = target.main;      // ← A 行
        int space = (int)mm.simulationSpace;             // ← B 行
        Debug.Log("[编译探针1] simulationSpace = " + space + " (0=World 1=Local 2=Custom)");
    }
}
