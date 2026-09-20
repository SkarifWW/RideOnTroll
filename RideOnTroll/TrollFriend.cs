using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TrollTamerMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class TrollTamerPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.custom.trolltamer";
        public const string PluginName = "TrollTamer";
        public const string PluginVersion = "1.2.0";

        private Harmony _harmony;

        private void Awake()
        {
            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();
            Logger.LogInfo("TrollTamer Mod loaded successfully.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
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
        public static bool IsTroll(Character character)
        {
            return character != null && character.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);
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
                    SetupTrollTameable(prefab);
                }
            }
        }

        public static void SetupTrollTameable(GameObject go)
        {
            Tameable tame = go.GetComponent<Tameable>();
            if (tame == null)
            {
                tame = go.AddComponent<Tameable>();
            }
            tame.m_commandable = true;
            tame.m_nameBeforeText = true;
            tame.m_tameText = "$hud_tamelove";
            tame.m_fedDuration = 1800f;
            tame.m_tamingTime = 60f;
        }

        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        public static void Character_Awake_Postfix(Character __instance)
        {
            if (IsTroll(__instance))
            {
                SetupTrollTameable(__instance.gameObject);
            }
        }

        // ПЕРЕХВАТ ВЗАИМОДЕЙСТВИЯ: Позволяет клавише "E" корректно отдавать команды Tameable
        [HarmonyPatch(typeof(Player), "Interact")]
        [HarmonyPrefix]
        public static bool Player_Interact_Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (go == null) return true;

            Character character = go.GetComponentInParent<Character>();
            if (IsTroll(character) && character.IsTamed())
            {
                Tameable tameable = character.GetComponent<Tameable>();
                if (tameable != null)
                {
                    if (tameable.Interact(__instance, hold, alt))
                    {
                        AccessTools.Method(typeof(Humanoid), "DoInteractAnimation").Invoke(__instance, new object[] { character.gameObject });
                    }
                    return false; // Отменяем стандартную обработку
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
}