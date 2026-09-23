using System;
using System.Collections.Generic;
using System.Reflection;
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
        public const string PluginVersion = "1.6.0";

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

            // приручили стойкостью — включаем кормление (хил)
            TrollTamePatches.AddTrollFood(gameObject);

            Player closestPlayer = Player.GetClosestPlayer(transform.position, 30f);
            if (closestPlayer != null)
            {
                closestPlayer.Message(MessageHud.MessageType.Center, $"{_character.m_name} покорен вашей стойкостью!", 0, null, false);
            }

            Destroy(this);
        }
    }

    // Хил от еды: ванильный подбор еды может не сработать (m_tamable у MonsterAI
    // заполняется в BaseAI.Awake раньше, чем мы добавляем Tameable троллю),
    // поэтому ищем и съедаем мясо сами: находим оленину в радиусе,
    // ведём тролля к ней (BaseAI.MoveTo), съедаем, лечим и сбрасываем голод.
    // Ест, когда голоден ИЛИ когда ранен (HP < 50%).
    public class TrollFoodHealer : MonoBehaviour
    {
        private Character m_character;
        private MonsterAI m_ai;
        private ZNetView m_nview;
        private ItemDrop m_target;

        public bool HasFoodTarget => m_target != null;

        private static readonly MethodInfo s_moveTo = AccessTools.Method(typeof(BaseAI), "MoveTo");
        private static readonly int s_itemMask = LayerMask.GetMask("item");
        private static readonly Collider[] s_buffer = new Collider[32];

        private float m_searchTimer;
        private const float SearchInterval = 1f;
        private const float EatRadius = 2.2f;
        private const float SearchRadius = 10f;

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_ai = GetComponent<MonsterAI>();
            m_nview = GetComponent<ZNetView>();
        }

        private void FixedUpdate()
        {
            try { Tick(Time.fixedDeltaTime); }
            catch (Exception e) { Debug.LogWarning("[TrollTamer] Feeder tick failed: " + e.Message); }
        }

        private void Tick(float dt)
        {
            if (m_character == null || m_ai == null || m_nview == null) return;
            if (!m_nview.IsValid() || !m_nview.IsOwner()) return;
            if (m_character.IsDead() || !m_character.IsTamed()) return;

            ZDO zdo = m_nview.GetZDO();
            if (zdo.GetBool(TrollWalkConstants.HashActive, false)) return; // в маршруте
            if (zdo.GetBool(TrollTamePatches.ZDO_FREEZE_KEY, false)) return; // заморожен
            if (m_ai.GetTargetCreature() != null) return; // в бою

            bool hungry = IsHungry(zdo);
            bool hurt = m_character.GetHealth() < m_character.GetMaxHealth() * 0.5f;
            if (!hungry && !hurt) { m_target = null; return; }

            m_searchTimer += dt;
            if (m_searchTimer >= SearchInterval || m_target == null)
            {
                m_searchTimer = 0f;
                m_target = FindClosestMeat();
            }

            if (m_target == null) return;
            if (!m_target || m_target.m_itemData == null) { m_target = null; return; }

            float dist = Vector3.Distance(m_character.transform.position, m_target.transform.position);
            if (dist > EatRadius)
            {
                s_moveTo?.Invoke(m_ai, new object[] { dt, m_target.transform.position, EatRadius * 0.8f, false });
                return;
            }

            Eat(m_target);
            m_target = null;
        }

        private bool IsHungry(ZDO zdo)
        {
            Tameable tame = GetComponent<Tameable>();
            float fedDuration = tame != null ? tame.m_fedDuration : 1800f;
            DateTime last = new DateTime(zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L));
            return (ZNet.instance.GetTime() - last).TotalSeconds > fedDuration * 0.5;
        }

        private ItemDrop FindClosestMeat()
        {
            Vector3 center = m_character.transform.position + Vector3.up;
            int n = Physics.OverlapSphereNonAlloc(center, SearchRadius, s_buffer, s_itemMask);
            ItemDrop best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider col = s_buffer[i];
                if (col == null || col.attachedRigidbody == null) continue;
                ItemDrop drop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) continue;
                ZNetView nv = drop.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                if (!IsMeat(drop)) continue;

                float d = Vector3.Distance(center, drop.transform.position);
                if (d < bestDist) { bestDist = d; best = drop; }
            }
            return best;
        }

        // Матчинг по имени префаба GO ("DeerMeat(Clone)") — не зависит от локализации
        private static bool IsMeat(ItemDrop drop)
        {
            string goName = drop.gameObject.name;
            if (goName.StartsWith("RawMeat") || goName.StartsWith("DeerMeat")) return true;

            string token = drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_name : "";
            string expected = TrollTamePatches.MeatItemName;
            return !string.IsNullOrEmpty(token) && token == expected;
        }

        private void Eat(ItemDrop drop)
        {
            ZNetView nv = drop.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
            if (!drop.RemoveOne()) return;

            ZDO zdo = m_nview.GetZDO();
            zdo.Set(ZDOVars.s_tameLastFeeding, ZNet.instance.GetTime().Ticks);

            float amount = m_character.GetMaxHealth() * 0.15f;
            HealCharacter(m_character, amount);

            Player closest = Player.GetClosestPlayer(transform.position, 30f);
            if (closest != null)
                closest.Message(MessageHud.MessageType.TopLeft, $"{m_character.m_name} подкрепился и восстановил силы", 0, null, false);
        }

        // Character.Heal через рефлексию (сигнатура могла меняться между версиями)
        private static void HealCharacter(Character c, float amount)
        {
            try
            {
                MethodInfo mi = AccessTools.Method(typeof(Character), "Heal");
                if (mi == null) return;
                ParameterInfo[] ps = mi.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i == 0) args[i] = amount;
                    else args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                }
                mi.Invoke(c, args);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollTamer] Heal failed: " + e.Message);
            }
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

            // РАЗМНОЖЕНИЕ: в этой версии игры им управляет компонент Procreation.
            // Сносим его у троллей — жёсткая гарантия отсутствия детёнышей.
            Procreation procreation = go.GetComponent<Procreation>();
            if (procreation != null) UnityEngine.Object.Destroy(procreation);

            // ЕДА: сырая оленина — только ПРИРУЧЁННЫМ (диким нельзя: подбор еды
            // с земли запускает ванильный тейминг в обход «стойкости»)
            AddTrollFood(go);

            if (go.GetComponent<TrollFreezeController>() == null)
            {
                go.AddComponent<TrollFreezeController>();
            }
        }

        // Кормление: оленина в MonsterAI.m_consumeItems + хил на поедание.
        // Вызывается при загрузке приручённого тролля и в момент приручения.
        public static void AddTrollFood(GameObject go)
        {
            try
            {
                // префабы (без ZDO) — пропускаем: IsTamed на них невалиден (NRE)
                ZNetView nv = go.GetComponent<ZNetView>();
                if (nv == null || nv.GetZDO() == null) return;

                Character c = go.GetComponent<Character>();
                if (c == null || !c.IsTamed()) return; // только приручённые

                MonsterAI ai = go.GetComponent<MonsterAI>();
                if (ai == null) return;

                string meatName = MeatItemName;
                GameObject meatPrefab = null;
                foreach (string prefabName in new[] { "DeerMeat", "RawMeat" })
                {
                    GameObject p = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
                    if (p != null) { meatPrefab = p; break; }
                }

                if (meatPrefab == null)
                {
                    Debug.LogWarning("[TrollTamer] Meat prefab not found (DeerMeat/RawMeat) — vanilla consumption disabled, feeder still active");
                }
                else
                {
                    ItemDrop meat = meatPrefab.GetComponent<ItemDrop>();
                    if (meat != null)
                    {
                        if (ai.m_consumeItems == null) ai.m_consumeItems = new List<ItemDrop>();
                        if (!ai.m_consumeItems.Contains(meat)) ai.m_consumeItems.Add(meat);
                    }
                }

                if (go.GetComponent<TrollFoodHealer>() == null)
                    go.AddComponent<TrollFoodHealer>();

                Debug.Log("[TrollTamer] Feeding enabled for tamed troll (" + meatName + ")");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollTamer] AddTrollFood failed: " + e.Message);
            }
        }

        private static string s_meatItemName;

        // Имя предмета еды (для сравнения по токену; первичный матчинг — по GO-имени)
        public static string MeatItemName
        {
            get
            {
                if (s_meatItemName == null)
                {
                    try
                    {
                        foreach (string prefabName in new[] { "DeerMeat", "RawMeat" })
                        {
                            GameObject p = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
                            ItemDrop d = p != null ? p.GetComponent<ItemDrop>() : null;
                            if (d != null && d.m_itemData != null && d.m_itemData.m_shared != null)
                            {
                                s_meatItemName = d.m_itemData.m_shared.m_name;
                                Debug.Log("[TrollTamer] Troll food item: " + s_meatItemName);
                                break;
                            }
                        }
                        if (s_meatItemName == null) s_meatItemName = "$item_deer_meat";
                    }
                    catch { s_meatItemName = "$item_deer_meat"; }
                }
                return s_meatItemName;
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

        // Игнорируем постройки на платформе при взаимодействии с троллем
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

    // Страховка размножения: у троллей Procreation снесён (см. SetupTrollComponents),
    // этот патч дополнительно глушит Procreation.Procreate(), если компонент
    // каким-то образом окажется на тролле (мод/консоль/обновление игры).
    [HarmonyPatch]
    public static class ProcreationNoBreed_Patch
    {
        private static MethodBase TargetMethod()
        {
            MethodBase m = AccessTools.Method(typeof(Procreation), "Procreate");
            if (m != null)
            {
                Debug.Log("[TrollTamer] Troll breeding block active on Procreation.Procreate");
                return m;
            }

            foreach (MethodInfo mi in typeof(Procreation).GetMethods(AccessTools.all))
            {
                if (mi.DeclaringType != typeof(Procreation)) continue;
                if (mi.IsGenericMethod) continue;
                if (mi.GetParameters().Length != 0) continue;
                string name = mi.Name;
                if (name.IndexOf("Procreat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.Log("[TrollTamer] Troll breeding block active on Procreation." + name);
                    return mi;
                }
            }

            Debug.LogWarning("[TrollTamer] Procreation.Procreate not found — breeding blocked by component removal only");
            return AccessTools.Method(typeof(ProcreationNoBreed_Patch), nameof(Stub));
        }

        private static void Stub() { }

        [HarmonyPrefix]
        private static bool Prefix(Procreation __instance)
        {
            Character c = __instance != null ? __instance.GetComponent<Character>() : null;
            if (c != null && TrollTamePatches.IsTroll(c))
            {
                UnityEngine.Object.Destroy(__instance); // самозачистка на всякий случай
                return false;
            }
            return true;
        }
    }
}