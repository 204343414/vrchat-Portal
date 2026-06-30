using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 传送枪 : UdonSharpBehaviour
{
    [Header("════════════ 传送门 ════════════")]
    [Tooltip("传送门A大级")]
    public Transform portalA;
    
    [Tooltip("传送门B大级")]
    public Transform portalB;

    [Header("════════════ 射线设置 ════════════")]
    [Tooltip("最大射程")]
    public float maxDistance = 50f;

    [Tooltip("射线发射点（空物体，Z轴正方向为发射方向）")]
    public Transform shootPoint;

    [Tooltip("可放置的层（墙壁、地板等）")]
    public LayerMask placementLayers = -1;

    [Tooltip("阻挡层（击中时无法放置传送门）")]
    public LayerMask blockedLayers;

    [Tooltip("传送门离墙面的偏移距离")]
    public float wallOffset = 0.01f;

    [Header("════════════ 冷却设置 ════════════")]
    [Tooltip("发射冷却时间（秒）")]
    public float cooldownTime = 0.5f;
    
    [Tooltip("冷却期间是否播放失败音效")]
    public bool playSoundOnCooldown = false;

    [Header("════════════ 动画控制器 ════════════")]
    [Tooltip("枪械动画控制器")]
    public Animator gunAnimator;
    
    [Tooltip("发射A门的Trigger名称")]
    public string shootTriggerA = "ShootA";
    
    [Tooltip("发射B门的Trigger名称")]
    public string shootTriggerB = "ShootB";
    
    [Tooltip("发射失败的Trigger名称")]
    public string shootTriggerFail = "ShootFail";

    [Header("════════════ 音效（可选）════════════")]
    public AudioSource audioSource;
    
    [Tooltip("A门随机音效数组")]
    public AudioClip[] shootSoundsA;
    
    [Tooltip("B门随机音效数组")]
    public AudioClip[] shootSoundsB;
    
    [Tooltip("发射失败音效数组")]
    public AudioClip[] failSounds;

    [Header("════════════ 标记的碰撞体 ════════════")]
    [Tooltip("传送门A射中的碰撞体（自动记录）")]
    public Collider markedColliderA;

    [Tooltip("传送门B射中的碰撞体（自动记录）")]
    public Collider markedColliderB;

    [Header("════════════ 调试 ════════════")]
    public bool showDebugRay = true;
    public Color rayColorA = Color.red;
    public Color rayColorB = Color.blue;
    public Color rayColorFail = Color.gray;

    [Tooltip("放置传送门时输出极简日志：按钮A/B、实际移动哪个Transform、是否给B额外旋转180、最终forward/up。")]
    public bool debugPortalGunLog = true;

    private VRCPlayerApi localPlayer;
    private bool isHeld = false;
    private float cooldownTimer = 0f;
    private bool vrTriggerLeftPressed = false;
    private bool vrTriggerRightPressed = false;

    void Start()
    {
        localPlayer = Networking.LocalPlayer;
    }

    public override void OnPickup()
    {
        isHeld = true;
    }

    public override void OnDrop()
    {
        isHeld = false;
    }

    void Update()
    {
        if (!isHeld) return;
        if (localPlayer == null || !localPlayer.IsValid()) return;

        if (cooldownTimer > 0)
        {
            cooldownTimer -= Time.deltaTime;
        }

        if (!localPlayer.IsUserInVR())
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            
            if (scroll > 0)
            {
                TryShootPortal(true);
            }
            else if (scroll < 0)
            {
                TryShootPortal(false);
            }
        }
        else
        {
            if (Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.7f && !vrTriggerLeftPressed)
            {
                vrTriggerLeftPressed = true;
                TryShootPortal(true);
            }
            else if (Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") < 0.3f)
            {
                vrTriggerLeftPressed = false;
            }
            
            if (Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.7f && !vrTriggerRightPressed)
            {
                vrTriggerRightPressed = true;
                TryShootPortal(false);
            }
            else if (Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") < 0.3f)
            {
                vrTriggerRightPressed = false;
            }
        }
    }

    void TryShootPortal(bool isPortalA)
    {
        if (cooldownTimer > 0)
        {
            if (playSoundOnCooldown)
            {
                PlayFailSound();
            }
            return;
        }

        Transform portal = isPortalA ? portalA : portalB;
        if (portal == null) return;
        if (shootPoint == null) return;

        Vector3 rayOrigin = shootPoint.position;
        Vector3 rayDirection = shootPoint.forward;

        LayerMask combinedLayers = placementLayers | blockedLayers;

        RaycastHit hit;
        if (Physics.Raycast(rayOrigin, rayDirection, out hit, maxDistance, combinedLayers))
        {
            int hitLayer = hit.collider.gameObject.layer;
            
            if (((1 << hitLayer) & blockedLayers) != 0)
            {
                OnShootFailed(rayOrigin, hit.point);
                return;
            }
            
            VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            
            Vector3 portalPos = hit.point + hit.normal * wallOffset;
            Quaternion portalRot = Quaternion.LookRotation(hit.normal, Vector3.up);
            
            if (Mathf.Abs(hit.normal.y) > 0.9f)
            {
                Vector3 playerForward = headData.rotation * Vector3.forward;
                playerForward.y = 0;
                playerForward.Normalize();
                
                if (hit.normal.y > 0)
                {
                    portalRot = Quaternion.LookRotation(hit.normal, playerForward);
                }
                else
                {
                    portalRot = Quaternion.LookRotation(hit.normal, -playerForward);
                }
            }
            
            bool appliedBHalfTurn = false;
            if (!isPortalA)
            {
                portalRot = portalRot * Quaternion.Euler(0, 180f, 0);
                appliedBHalfTurn = true;
            }
            
            portal.position = portalPos;
            portal.rotation = portalRot;

            if (debugPortalGunLog)
            {
                Debug.Log(
                    "[传送枪] " + (isPortalA ? "按钮A" : "按钮B") +
                    " -> " + portal.name +
                    " half180=" + appliedBHalfTurn +
                    " hitN=" + hit.normal +
                    " pos=" + portal.position +
                    " rot=" + portal.rotation.eulerAngles +
                    " fwd=" + portal.forward +
                    " up=" + portal.up +
                    " col=" + hit.collider.name +
                    " layer=" + hit.collider.gameObject.layer
                );
            }

            // 记录打中的碰撞体
            if (isPortalA)
            {
                markedColliderA = hit.collider;
            }
            else
            {
                markedColliderB = hit.collider;
            }

            cooldownTimer = cooldownTime;

            Color debugColor = isPortalA ? rayColorA : rayColorB;
            AudioClip[] sounds = isPortalA ? shootSoundsA : shootSoundsB;
            string triggerName = isPortalA ? shootTriggerA : shootTriggerB;

            if (audioSource != null && sounds != null && sounds.Length > 0)
            {
                int randomIndex = Random.Range(0, sounds.Length);
                if (sounds[randomIndex] != null)
                {
                    audioSource.PlayOneShot(sounds[randomIndex]);
                }
            }

            if (gunAnimator != null && !string.IsNullOrEmpty(triggerName))
            {
                gunAnimator.SetTrigger(triggerName);
            }

            if (showDebugRay)
            {
                Debug.DrawLine(rayOrigin, hit.point, debugColor, 1f);
            }
        }
        else
        {
            OnShootFailed(rayOrigin, rayOrigin + rayDirection * maxDistance);
        }
    }

    void OnShootFailed(Vector3 rayOrigin, Vector3 hitPoint)
    {
        cooldownTimer = cooldownTime;

        PlayFailSound();

        if (gunAnimator != null && !string.IsNullOrEmpty(shootTriggerFail))
        {
            gunAnimator.SetTrigger(shootTriggerFail);
        }

        if (showDebugRay)
        {
            Debug.DrawLine(rayOrigin, hitPoint, rayColorFail, 1f);
        }
    }

    void PlayFailSound()
    {
        if (audioSource != null && failSounds != null && failSounds.Length > 0)
        {
            int randomIndex = Random.Range(0, failSounds.Length);
            if (failSounds[randomIndex] != null)
            {
                audioSource.PlayOneShot(failSounds[randomIndex]);
            }
        }
    }

    public bool IsOnCooldown()
    {
        return cooldownTimer > 0;
    }

    public float GetCooldownProgress()
    {
        if (cooldownTime <= 0) return 1f;
        return 1f - (cooldownTimer / cooldownTime);
    }

    public float GetRemainingCooldown()
    {
        return Mathf.Max(0, cooldownTimer);
    }

    public Collider GetMarkedColliderA()
    {
        return markedColliderA;
    }
    
    public Collider GetMarkedColliderB()
    {
        return markedColliderB;
    }
    
    public void SetMarkedColliderAEnabled(bool enabled)
    {
        if (markedColliderA != null)
        {
            markedColliderA.enabled = enabled;
        }
    }
    
    public void SetMarkedColliderBEnabled(bool enabled)
    {
        if (markedColliderB != null)
        {
            markedColliderB.enabled = enabled;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!showDebugRay) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, maxDistance);
    }
}
