using System;
using BepInEx;
using HarmonyLib;
using TrollBuildingMod;
using UnityEngine;

namespace TrollTamerMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class TrollTamerPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.custom.trolltamer";
        public const string PluginName = "TrollTamer";
        public const string PluginVersion = "1.4.0";

        private Harmony _harmony;

        private void Awake()
        {
            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();
            Logger.LogInfo("TrollTamer & RideOnTroll загружен успешно.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    public class TrollFreezeController : MonoBehaviour
    {
        private Character _character;
        private ZNetView _nview;
        private Rigidbody _body;
        private ZSyncAnimation _zanim;
        private Animator[] _animators;

        private Vector3 _lockedPosition;
        private Quaternion _lockedRotation;
        private bool _isCurrentlyFrozen = false;

        private void Awake()
        {
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _body = GetComponent<Rigidbody>();
            _zanim = GetComponent<ZSyncAnimation>();
            _animators = GetComponentsInChildren<Animator>(true);
        }

        private void Start()
        {
            _animators = GetComponentsInChildren<Animator>(true);
        }

        private void FixedUpdate()
        {
            if (_nview == null || !_nview.IsValid()) return;

            bool shouldBeFrozen = _character.IsTamed() && _nview.GetZDO().GetBool(TrollTamePatches.ZDO_FREEZE_KEY, false);

            if (shouldBeFrozen)
            {
                if (!_isCurrentlyFrozen)
                {
                    _isCurrentlyFrozen = true;
                    _lockedPosition = transform.position;
                    _lockedRotation = transform.rotation;

                    if (_body != null)
                    {
                        _body.linearVelocity = Vector3.zero;
                        _body.angularVelocity = Vector3.zero;
                        _body.isKinematic = true;
                    }
                }

                if (_zanim != null && _zanim.enabled)
                {
                    _zanim.enabled = false;
                }

                if (_animators != null)
                {
                    for (int i = 0; i < _animators.Length; i++)
                    {
                        if (_animators[i] != null && _animators[i].enabled)
                        {
                            _animators[i].enabled = false;
                        }
                    }
                }

                transform.position = _lockedPosition;
                transform.rotation = _lockedRotation;
            }
            else
            {
                if (_isCurrentlyFrozen)
                {
                    _isCurrentlyFrozen = false;

                    if (_body != null)
                    {
                        _body.isKinematic = false;
                        _body.linearVelocity = Vector3.zero;
                        _body.angularVelocity = Vector3.zero;
                    }

                    if (_zanim != null && !_zanim.enabled)
                    {
                        _zanim.enabled = true;
                    }

                    if (_animators != null)
                    {
                        for (int i = 0; i < _animators.Length; i++)
                        {
                            if (_animators[i] != null && !_animators[i].enabled)
                            {
                                _animators[i].enabled = true;
                            }
                        }
                    }
                }
            }
        }
    }

    public class TrollTamingTracker : MonoBehaviour
    {
        private MonsterAI _monsterAI;
        private Character _character;
        private ZNetView _nview;

        private float _timer = 0f;
        private const float RequiredTime = 60f;
        public const string FailedKey = "TrollHitByPlayer";

        private void Awake()
        {
            _monsterAI = GetComponent<MonsterAI>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner())
            {
                Destroy(this);
                return;
            }

            if (_character.IsDead() || _character.IsTamed() || _nview.GetZDO().GetBool(FailedKey, false))
            {
                Destroy(this);
                return;
            }

            Character target = _monsterAI.GetTargetCreature();

            if (target != null && target.IsPlayer() && _monsterAI.IsAlerted())
            {
                _timer += Time.deltaTime;
                if (_timer >= RequiredTime)
                {
                    CompleteTaming();
                }
            }
            else
            {
                Destroy(this);
            }
        }

        public void AbortPermanently()
        {
            _nview?.GetZDO()?.Set(FailedKey, true);
            Destroy(this);
        }

        private void CompleteTaming()
        {
            _monsterAI.MakeTame();

            Tameable tameable = GetComponent<Tameable>();
            if (tameable != null)
            {
                tameable.m_commandable = true;
                tameable.m_fedDuration = 1800f;
            }

            Player closestPlayer = Player.GetClosestPlayer(transform.position, 30f);
            if (closestPlayer != null)
            {
                closestPlayer.Message(MessageHud.MessageType.Center, $"{_character.m_name} покорен вашей стойкостью!", 0, null, false);
            }

            Destroy(this);
        }
    }

    [HarmonyPatch]
    public static class TrollTamePatches
    {
        public const string ZDO_FREEZE_KEY = "TrollBuild_IsFrozen";

        public static bool IsTroll(Character character)
        {
            return character != null && character.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFrozenTroll(Character character)
        {
            if (!IsTroll(character) || !character.IsTamed()) return false;
            ZNetView nv = character.GetComponent<ZNetView>();
            return nv != null && nv.IsValid() && nv.GetZDO().GetBool(ZDO_FREEZE_KEY, false);
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        public static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            if (__instance == null) return;

            string[] trollPrefabs = { "Troll", "Troll_Log" };
            foreach (string name in trollPrefabs)
            {
                GameObject prefab = __instance.GetPrefab(name);
                if (prefab != null)
                {
                    SetupTrollComponents(prefab);
                }
            }
        }

        public static void SetupTrollComponents(GameObject go)
        {
            Tameable tame = go.GetComponent<Tameable>() ?? go.AddComponent<Tameable>();
            tame.m_commandable = true;
            tame.m_nameBeforeText = true;
            tame.m_tameText = "$hud_tamelove";
            tame.m_fedDuration = 1800f;
            tame.m_tamingTime = 60f;

            if (go.GetComponent<TrollFreezeController>() == null)
            {
                go.AddComponent<TrollFreezeController>();
            }
        }

        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        public static void Character_Awake_Postfix(Character __instance)
        {
            if (IsTroll(__instance))
            {
                SetupTrollComponents(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Character), "CustomFixedUpdate")]
        [HarmonyPrefix]
        public static bool Character_CustomFixedUpdate_Prefix(Character __instance)
        {
            if (IsFrozenTroll(__instance))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        [HarmonyPrefix]
        public static bool MonsterAI_UpdateAI_Prefix(MonsterAI __instance)
        {
            Character c = __instance.GetComponent<Character>();
            if (IsFrozenTroll(c))
            {
                __instance.StopMoving();
                return false;
            }
            return true;
        }

        // ИСПРАВЛЕНИЕ: Игнорируем постройки на платформе при взаимодействии с троллем
        [HarmonyPatch(typeof(Player), "Interact")]
        [HarmonyPrefix]
        public static bool Player_Interact_Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (go == null) return true;

            // Если игрок взаимодействует с объектом платформы (дверь, сундук и т.д.), не перехватываем
            if (go.GetComponentInParent<TrollPieceTag>() != null)
            {
                return true;
            }

            Character character = go.GetComponentInParent<Character>();
            if (IsTroll(character) && character.IsTamed())
            {
                Tameable tameable = character.GetComponent<Tameable>();
                if (tameable != null)
                {
                    if (tameable.Interact(__instance, hold, alt))
                    {
                        AccessTools.Method(typeof(Humanoid), "DoInteractAnimation")?.Invoke(__instance, new object[] { character.gameObject });
                    }
                    return false;
                }
            }
            return true;
        }

        [HarmonyPatch(typeof(Character), "GetHoverText")]
        [HarmonyPostfix]
        public static void Character_GetHoverText_Postfix(Character __instance, ref string __result)
        {
            if (IsTroll(__instance) && __instance.IsTamed())
            {
                Tameable tame = __instance.GetComponent<Tameable>();
                if (tame != null)
                {
                    __result = tame.GetHoverText();
                }

                ZNetView nv = __instance.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid())
                {
                    bool isFrozen = nv.GetZDO().GetBool(ZDO_FREEZE_KEY, false);
                    string stateText = isFrozen
                        ? "<color=#55FF55>ВКЛ</color>"
                        : "<color=#FF5555>ВЫКЛ</color>";

                    __result += $"\n[<color=yellow><b>Зажать $KEY_Use</b></color>] Не двигайся: {stateText}";
                }
            }
        }

        [HarmonyPatch(typeof(Character), "GetHoverName")]
        [HarmonyPostfix]
        public static void Character_GetHoverName_Postfix(Character __instance, ref string __result)
        {
            if (IsTroll(__instance) && __instance.IsTamed())
            {
                Tameable tame = __instance.GetComponent<Tameable>();
                if (tame != null)
                {
                    __result = tame.GetHoverName();
                }
            }
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        [HarmonyPostfix]
        public static void MonsterAI_UpdateTarget_Postfix(MonsterAI __instance, Character ___m_targetCreature)
        {
            if (!IsTroll(__instance.GetComponent<Character>())) return;

            Character character = __instance.GetComponent<Character>();
            if (character == null || character.IsTamed()) return;

            if (___m_targetCreature != null && ___m_targetCreature.IsPlayer())
            {
                ZNetView nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                if (nview.GetZDO().GetBool(TrollTamingTracker.FailedKey, false)) return;

                if (__instance.GetComponent<TrollTamingTracker>() == null)
                {
                    __instance.gameObject.AddComponent<TrollTamingTracker>();
                }
            }
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        [HarmonyPrefix]
        public static void Character_RPC_Damage_Prefix(Character __instance, HitData hit)
        {
            if (!IsTroll(__instance)) return;
            if (__instance.IsTamed()) return;

            Character attacker = hit.GetAttacker();
            if (attacker != null && attacker.IsPlayer())
            {
                ZNetView nview = __instance.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    nview.GetZDO().Set(TrollTamingTracker.FailedKey, true);

                    TrollTamingTracker tracker = __instance.GetComponent<TrollTamingTracker>();
                    tracker?.AbortPermanently();

                    (attacker as Player)?.Message(MessageHud.MessageType.TopLeft, "Тролль разъярен полученным ударом! Приручение сорвано навсегда.", 0, null, false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player), "Update")]
    public static class Player_HoldE_TrollFreeze_Patch
    {
        private static float _holdTimer = 0f;
        private static bool _holdExecuted = false;
        private const float HOLD_REQUIRED_TIME = 0.6f;

        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            if (!ZInput.GetButton("Use") && !ZInput.GetButton("JoyUse"))
            {
                _holdTimer = 0f;
                _holdExecuted = false;
                return;
            }

            GameObject hoverObj = __instance.GetHoverObject();
            if (hoverObj == null) return;

            // Если смотрим на постройку на тролле — не переключаем заморозку
            if (hoverObj.GetComponentInParent<TrollPieceTag>() != null) return;

            Character character = hoverObj.GetComponentInParent<Character>();
            if (character == null || !TrollTamePatches.IsTroll(character) || !character.IsTamed()) return;

            ZNetView nview = character.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            _holdTimer += Time.deltaTime;
            if (_holdTimer >= HOLD_REQUIRED_TIME && !_holdExecuted)
            {
                _holdExecuted = true;
                ToggleFreeze(character, nview);
            }
        }

        private static void ToggleFreeze(Character character, ZNetView nview)
        {
            bool currentState = nview.GetZDO().GetBool(TrollTamePatches.ZDO_FREEZE_KEY, false);
            bool newState = !currentState;

            nview.ClaimOwnership();
            nview.GetZDO().Set(TrollTamePatches.ZDO_FREEZE_KEY, newState);

            string message = newState
                ? "Режим неподвижности: ВКЛЮЧЕН"
                : "Режим неподвижности: ВЫКЛЮЧЕН";

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, message);
        }
    }
}