using UdonSharp;
using UnityEngine;

// ============================================================
// 粒子触发检测实验（测试探针，验证后可删除）
// ============================================================
// 验证目标："用触发器/Trigger模块检测粒子穿越传送门"方案在 Udon 里是否可行。
//
// 已联网查证的事实（2026-08-16）：
//   1. 粒子不会触发 OnTriggerEnter/OnCollisionEnter（粒子不是物理刚体）；
//   2. OnParticleCollision 只认【非触发器】碰撞体，且要求粒子系统 Collision
//      模块开启 Send Collision Messages；
//   3. Unity 粒子系统自带 Trigger 模块（粒子系统组件面板内，独立于碰撞模块），
//      可指定碰撞体列表并按 Enter/Exit/Inside/Outside 回调 OnParticleTrigger。
//   4. 但 OnParticleTrigger 是无参回调——不告知哪颗粒子、什么位置，
//      真要传送仍需扫描粒子缓冲（本管理器每帧已在做的事）。
//
// 实验步骤（30秒裁决）：
//   本文件已激活 OnParticleTrigger override，同步进工程后看 UdonSharp 编译结果：
//      - 编译报错"no suitable method found to override"之类
//        → UdonSharpBehaviour 没声明该事件 → Udon 不接线 → 触发器方案不可行；
//      - 编译通过 → 事件被 Udon 接线 → 把本脚本挂到粒子系统所在物体，
//        在粒子系统 Trigger 模块里拖入一个测试碰撞体、勾 Enter + Callback，
//        运行看 Console 有没有日志 → 有的话我们再评估值不值得用它。
//   注意：若编译报错，删除本文件即可恢复，报错本身就是裁决结果。
// ============================================================

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 粒子触发实验 : UdonSharpBehaviour
{
    public override void OnParticleTrigger()
    {
        Debug.Log("[粒子触发实验] OnParticleTrigger 触发了！");
    }
}
