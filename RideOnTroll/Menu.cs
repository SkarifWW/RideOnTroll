using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

// ============================================================================
//  MENU.CS v5 — Радиальное меню (в стиле ванильного) + добыча ресурсов.
//
//  Y на тролле — открыть. Верх: Не двигаться | Лево: Лес | Право: Камень | Низ: Маршрут
//  ЛКМ — выбрать, ПКМ / Y / Esc — закрыть.
//
//  ДОБЫЧА: цель выбирается ПО ТАБЛИЦЕ ДРОПА объекта (DropOnDestroyed /
//  m_dropList) — какой предмет реально выпадает, тот и добывается.
//  Имя префаба используется только как запасной вариант. Дроп-таблицы
//  кэшируются по префабу.
// ============================================================================
namespace TrollTamerMod
{
    public enum GatherType
    {
        None = 0,
        Wood = 1,      // $item_wood      — Древесина
        FineWood = 2,  // $item_finewood  — Качественная древесина
        CoreWood = 3,  // $item_corewood  — Цельная древесина
        Stone = 4,     // $item_stone     — Камень
        Copper = 5     // $item_copperore — Медная руда
    }

    public static class TrollMenuState
    {
        public static bool IsOpen;
        public static ZDOID TrollID;
        internal static bool RouteBypass;
    }

    // ================================================================
    //  ПОДБОР ЦЕЛЕЙ ПО ТАБЛИЦЕ ДРОПА
    // ================================================================
    internal static class TrollResourceMatcher
    {
        // кэш: префаб -> имена предметов дропа (null = дропа нет)
        private static readonly Dictionary<string, string[]> s_dropCache =
            new Dictionary<string, string[]>();

        // кэш: тип|префаб -> подходит ли
        private static readonly Dictionary<string, bool> s_matchCache =
            new Dictionary<string, bool>();

        private static readonly Type s_treeLogType = FindTypeByName("TreeLog");
        private static readonly Type s_treeSyncType = FindTypeByName("TreeSync");

        private static Type FindTypeByName(string name)
        {
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType(name, false);
                    if (t != null && typeof(MonoBehaviour).IsAssignableFrom(t))
                        return t;
                }
            }
            catch { }
            return null;
        }

        public static string ItemTokenFor(GatherType type)
        {
            switch (type)
            {
                case GatherType.Wood: return "$item_wood";
                case GatherType.FineWood: return "$item_finewood";
                case GatherType.CoreWood: return "$item_corewood";
                case GatherType.Stone: return "$item_stone";
                case GatherType.Copper: return "$item_copperore";
                default: return "";
            }
        }

        // Разрушаемый компонент на коллайдере (дерево/бревно/руда/деструктибл)
        public static MonoBehaviour FindDestructible(Collider c)
        {
            Component comp = c.GetComponentInParent<TreeBase>();
            if (comp == null && s_treeLogType != null) comp = c.GetComponentInParent(s_treeLogType);
            if (comp == null && s_treeSyncType != null) comp = c.GetComponentInParent(s_treeSyncType);
            if (comp == null) comp = c.GetComponentInParent<MineRock>();
            if (comp == null) comp = c.GetComponentInParent<MineRock5>();
            if (comp == null) comp = c.GetComponentInParent<Destructible>();
            if (comp is MonoBehaviour mb && comp is IDestructible) return mb;
            return null;
        }

        // Имя префаба через ZNetView — не зависит от имени коллайдера
        public static string GetRootPrefabName(Component c)
        {
            try
            {
                ZNetView nv = c.GetComponentInParent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.GetZDO() != null && ZNetScene.instance != null)
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(nv.GetZDO().GetPrefab());
                    if (prefab != null) return prefab.name;
                }
            }
            catch { }
            return Utils.GetPrefabName(c.gameObject);
        }

        public static bool TargetMatches(MonoBehaviour destr, GatherType type)
        {
            try
            {
                string prefab = GetRootPrefabName(destr) ?? "";
                string key = ((int)type) + "|" + prefab;
                if (s_matchCache.TryGetValue(key, out bool cached)) return cached;

                bool result = ComputeMatch(destr, prefab, type);
                s_matchCache[key] = result;
                return result;
            }
            catch { return false; }
        }

        private static bool ComputeMatch(MonoBehaviour destr, string prefab, GatherType type)
        {
            string token = ItemTokenFor(type);
            if (string.IsNullOrEmpty(token)) return false;

            // 1) ИСТИНА В ПОСЛЕДНЕЙ ИНСТАНЦИИ — таблица дропа.
            //    Если дроп известен и нашего предмета в нём нет — объект
            //    не подходит ТОЧНО (даже если имя похоже).
            string[] drops = GetDropNames(prefab, destr);
            if (drops != null)
            {
                foreach (string d in drops)
                    if (string.Equals(d, token, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }

            // 2) Фоллбэк по точному имени префаба (если дроп не читается)
            return FallbackNameMatch(prefab, type);
        }

        // ---------- чтение таблицы дропа (рефлексия + кэш) ----------

        private static string[] GetDropNames(string prefab, MonoBehaviour sample)
        {
            if (s_dropCache.ContainsKey(prefab)) return s_dropCache[prefab];

            HashSet<string> names = new HashSet<string>();
            try
            {
                // деревья/деструктиблы: DropOnDestroyed компонент
                foreach (DropOnDestroyed dod in sample.GetComponentsInChildren<DropOnDestroyed>(true))
                {
                    CollectFromValue(dod, names, 0);
                    if (names.Count > 0) break;
                }

                // MineRock5 / MineRock: DropTable в полях компонента
                if (names.Count == 0)
                    CollectFromValue(sample, names, 0);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollMenu] Drop scan failed for " + prefab + ": " + e.Message);
            }

            string[] result = names.Count > 0 ? names.ToArray() : null;
            s_dropCache[prefab] = result;
            Debug.Log("[TrollMenu] Drops " + prefab + ": " +
                (result != null ? string.Join(", ", result) : "(none)"));
            return result;
        }

        // Рекурсивно ищем ссылки на ItemDrop в полях/списках объекта
        // (DropOnDestroyed.m_dropList -> DropData.m_item, MineRock5.m_dropList и т.д.)
        private static void CollectFromValue(object val, HashSet<string> names, int depth)
        {
            if (val == null || depth > 4 || names.Count > 24) return;

            if (val is ItemDrop itemDrop)
            {
                if (itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null)
                    names.Add(itemDrop.m_itemData.m_shared.m_name);
                return;
            }

            if (val is GameObject go)
            {
                ItemDrop id = go.GetComponent<ItemDrop>();
                if (id != null && id.m_itemData != null && id.m_itemData.m_shared != null)
                    names.Add(id.m_itemData.m_shared.m_name);
                return;
            }

            Type t = val.GetType();
            if (t.IsValueType || t == typeof(string) || t.IsEnum) return;
            if (val is Transform || val is Material || val is Texture || val is Mesh ||
                val is Shader || val is AudioClip || val is Animator ||
                val is Animation || val is AnimationClip || val is Rigidbody) return;

            if (val is System.Collections.IEnumerable en)
            {
                foreach (object o in en)
                {
                    if (o == null || o is ValueType || o is string) continue;
                    if (names.Count > 24) break;
                    CollectFromValue(o, names, depth + 1);
                }
                return;
            }

            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (names.Count > 24) break;
                try
                {
                    object fv = f.GetValue(val);
                    if (fv == null) continue;
                    if (fv is Transform || fv is Material || fv is Texture || fv is Mesh ||
                        fv is Shader || fv is AudioClip || fv is Animator) continue;
                    CollectFromValue(fv, names, depth + 1);
                }
                catch { }
            }
        }

        // ---------- фоллбэк по точным именам (только если дроп не читается) ----------

        private static readonly HashSet<string> FallbackWood = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Beech1", "Beech_small1", "Beech_small2", "Beech_Stub", "beech_log", "beech_log_half",
          "BirchStub", "OakStub", "AshlandsTreeStump1", "AshlandsTreeStump2", "AshlandsTreeStump3" };

        private static readonly HashSet<string> FallbackFine = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Birch1", "Birch1_aut", "Birch2", "Birch2_aut", "Birch_log", "Birch_log_half", "Oak1" };

        private static readonly HashSet<string> FallbackCore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Pinetree_01", "Pinetree_01_Stub", "PineTree_log", "PineTree_log_half",
          "FirTree", "FirTree_Stub", "FirTree_log", "FirTree_log_half" };

        private static readonly HashSet<string> FallbackStone = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "rock1_mountain", "rock2_heath", "rock2_mountain", "rock3_mountain",
          "rock4_coast", "rock4_forest", "rock4_heath", "MineRock_Stone",
          "Rock_3", "Rock_4", "Rock_7", "BigRock", "rock_mistlands1" };

        private static readonly HashSet<string> FallbackCopper = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "rock4_copper", "MineRock_Copper" };

        private static bool FallbackNameMatch(string prefab, GatherType type)
        {
            switch (type)
            {
                case GatherType.Wood: return FallbackWood.Contains(prefab);
                case GatherType.FineWood: return FallbackFine.Contains(prefab);
                case GatherType.CoreWood: return FallbackCore.Contains(prefab);
                case GatherType.Stone: return FallbackStone.Contains(prefab);
                case GatherType.Copper: return FallbackCopper.Contains(prefab);
                default: return false;
            }
        }
    }

    // ================================================================
    //  КОНТРОЛЛЕР ДОБЫЧИ
    // ================================================================
    public class TrollGatherController : MonoBehaviour
    {
        private Character m_character;
        private ZNetView m_nview;
        private MonsterAI m_ai;

        public const string ZDO_GATHER_KEY = "TrollGather_Type";
        public static readonly int HashGatherKey = ZDO_GATHER_KEY.GetStableHashCode();

        private MonoBehaviour m_target;
        private float m_searchTimer;
        private float m_attackTimer;

        // контроль прогресса (лечит «вечную ходьбу» к недостижимой цели)
        private float m_progressCheckTimer;
        private float m_lastTargetDist = float.MaxValue;
        private int m_noProgressCount;
        private int m_abandonCount;
        private Vector3 m_lastStuckPos;
        private int m_searchFailCount;
        private float m_lastMsgTime = -30f;

        private const float SearchInterval = 2f;
        private const float SearchRadius = 80f;
        private const float AttackRange = 5.5f;
        private const float AttackInterval = 1.3f;
        private const float AttackDamage = 250f;
        private const int AttackToolTier = 4;
        private const int MaxSearchFails = 3;        // подряд без кандидатов -> стоп
        private const int MaxAbandons = 3;          // подряд брошенных целей -> стоп
        private const float ProgressWindow = 5f;     // окно контроля прогресса, сек
        private const float ProgressMinGain = 2f;   // мин. приближение за окно, м

        private static readonly int s_searchMask =
            LayerMask.GetMask("Default", "static_solid", "Default_small");
        private static readonly Collider[] s_overlap = new Collider[512];
        private static readonly MethodInfo s_moveTo = AccessTools.Method(typeof(BaseAI), "MoveTo");

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_nview = GetComponent<ZNetView>();
            m_ai = GetComponent<MonsterAI>();
        }

        private void Update()
        {
            try { Tick(Time.deltaTime); }
            catch (Exception e) { Debug.LogWarning("[TrollMenu] Gather tick: " + e.Message); }
        }

        private void Tick(float dt)
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner()) return;
            if (m_character == null || m_character.IsDead()) { StopGather(); return; }
            if (m_ai != null && m_ai.GetTargetCreature() != null) return; // бой важнее

            int type = m_nview.GetZDO().GetInt(HashGatherKey, 0);
            if (type <= 0) { StopGather(); return; }

            m_searchTimer += dt;
            if (m_searchTimer >= SearchInterval || (m_target == null && m_searchTimer >= 1f))
            {
                m_searchTimer = 0;
                FindTarget((GatherType)type);
            }

            MonoBehaviour mb = m_target;
            if (mb == null || !mb.gameObject.activeInHierarchy) { m_target = null; return; }

            Vector3 targetPos = mb.transform.position;
            float dist = Utils.DistanceXZ(targetPos, transform.position);

            if (dist > AttackRange)
            {
                if (m_ai != null)
                {
                    Vector3 dir = targetPos - transform.position;
                    dir.y = 0;
                    if (dir.sqrMagnitude > 0.01f)
                    {
                        dir.Normalize();
                        bool run = dist > 15f;
                        try { s_moveTo?.Invoke(m_ai, new object[] { dt, targetPos, AttackRange * 0.8f, run }); }
                        catch { m_ai.MoveTowards(dir, run); }
                    }
                }
                TrackProgress(dist, dt);
            }
            else
            {
                m_ai?.StopMoving();
                m_attackTimer += dt;
                if (m_attackTimer >= AttackInterval)
                {
                    m_attackTimer = 0;
                    PerformAttack(mb);
                }
            }
        }

        private void FindTarget(GatherType type)
        {
            Vector3 center = transform.position + Vector3.up * 2f;
            int n = Physics.OverlapSphereNonAlloc(center, SearchRadius, s_overlap, s_searchMask);
            MonoBehaviour best = null;
            float bestDist = float.MaxValue;
            HashSet<UnityEngine.Object> seen = new HashSet<UnityEngine.Object>();

            for (int i = 0; i < n; i++)
            {
                Collider c = s_overlap[i];
                if (c == null || c.isTrigger) continue;

                MonoBehaviour destr = TrollResourceMatcher.FindDestructible(c);
                if (destr == null) continue;
                if (!seen.Add(destr)) continue;                     // дедуп по объекту
                if (destr.transform.IsChildOf(transform)) continue;  // своё
                if (!TrollResourceMatcher.TargetMatches(destr, type)) continue;

                float dist = Utils.DistanceXZ(destr.transform.position, transform.position);
                if (dist < bestDist) { bestDist = dist; best = destr; }
            }

            if (best != null)
            {
                if (m_target != best)
                {
                    m_target = best;
                    m_lastTargetDist = float.MaxValue;
                    m_progressCheckTimer = 0;
                    m_noProgressCount = 0;
                    m_lastStuckPos = transform.position;
                }
                m_searchFailCount = 0;
            }
            else if (m_target == null)
            {
                m_searchFailCount++;
                if (m_searchFailCount >= MaxSearchFails)
                {
                    Notify("Рядом нет подходящих ресурсов");
                    StopGather();
                }
            }
        }

        // Если за ProgressWindow секунд не приблизились к цели хотя бы на
        // ProgressMinGain метров — цель считается недостижимой и отбрасывается.
        private void TrackProgress(float dist, float dt)
        {
            m_progressCheckTimer += dt;
            if (m_progressCheckTimer < ProgressWindow) return;

            m_progressCheckTimer = 0;
            float gain = m_lastTargetDist - dist;

            if (gain < ProgressMinGain)
            {
                m_noProgressCount++;
                if (m_noProgressCount >= 2)
                {
                    m_noProgressCount = 0;
                    m_abandonCount++;
                    m_target = null;
                    m_lastTargetDist = float.MaxValue;

                    if (m_abandonCount >= MaxAbandons)
                    {
                        m_abandonCount = 0;
                        Notify("Тролль не может добраться до ресурсов");
                        StopGather();
                    }
                }
            }
            else
            {
                m_noProgressCount = 0;
                m_abandonCount = 0;
            }

            m_lastTargetDist = dist;
        }

        private void PerformAttack(MonoBehaviour target)
        {
            if (m_character == null) return;

            try { if (!m_character.InAttack()) m_character.StartAttack(null, false); }
            catch { }

            if (!(target is IDestructible destr)) return;
            try
            {
                HitData hit = new HitData();
                hit.m_damage.m_damage = AttackDamage;
                hit.m_toolTier = AttackToolTier;
                hit.m_point = target.transform.position + Vector3.up;
                hit.m_dir = Vector3.up;
                hit.m_hitType = HitData.HitType.EnemyHit;
                destr.Damage(hit);
            }
            catch (Exception e) { Debug.LogWarning("[TrollMenu] Attack failed: " + e.Message); }
        }

        private void StopGather()
        {
            try
            {
                if (m_nview != null && m_nview.IsValid() && m_nview.GetZDO() != null && m_nview.IsOwner())
                    m_nview.GetZDO().Set(HashGatherKey, 0);
            }
            catch { }
            Destroy(this);
        }

        private void Notify(string msg)
        {
            if (Time.time - m_lastMsgTime < 10f) return;
            m_lastMsgTime = Time.time;
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, msg, 0, null, false);
        }

        public static void Start(ZNetView trollNview, GatherType type)
        {
            if (trollNview == null || !trollNview.IsValid()) return;

            if (!trollNview.IsOwner()) trollNview.ClaimOwnership();
            ZDO zdo = trollNview.GetZDO();
            if (zdo == null) return;

            // отменяем маршрут (рулевая система платформы работает и без него)
            zdo.Set(TrollBuildingMod.TrollWalkConstants.HashActive, false);

            int current = zdo.GetInt(HashGatherKey, 0);
            if (current == (int)type)
            {
                zdo.Set(HashGatherKey, 0);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Добыча остановлена", 0, null, false);
                return;
            }

            zdo.Set(HashGatherKey, (int)type);

            TrollGatherController ctrl = trollNview.GetComponent<TrollGatherController>();
            if (ctrl == null) ctrl = trollNview.gameObject.AddComponent<TrollGatherController>();
            ctrl.m_target = null;
            ctrl.m_searchTimer = SearchInterval;
            ctrl.m_searchFailCount = 0;
            ctrl.m_abandonCount = 0;
            ctrl.m_lastTargetDist = float.MaxValue;

            string[] names = { "", "древесину", "качественную древесину", "цельную древесину", "камень", "медь" };
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Тролль добывает: {names[(int)type]}", 0, null, false);
        }
    }

    // ================================================================
    //  РАДИАЛЬНОЕ МЕНЮ v5 (x2 размер, улучшенное качество)
    // ================================================================
    public class TrollRadialMenu : MonoBehaviour
    {
        public static TrollRadialMenu Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.m_active;

        // ---- геометрия (x2 от v4) ----
        private const float ItemRadius = 265f;      // радиус расположения кнопок
        private const float MainButtonSize = 108f;
        private const float SubButtonSize = 84f;
        private const float LabelOffset = 76f;
        private const float DeadZone = 35f;
        private const float OpenGrace = 0.3f;
        private const float ExpandDelay = 0.15f;
        private const float CollapseDelay = 0.35f;

        private class MenuItem
        {
            public string Name;
            public string Description;
            public Sprite Icon;
            public Action OnSelect;
            public float Angle;      // 0=право, 90=верх
            public bool IsSubItem;
            public string GroupId;
            public bool Visible;
            public GameObject Root;
            public Image GlowImage;
            public Image FillImage;
            public Image BorderImage;
            public Image IconImage;
            public Text LabelText;
        }

        private readonly List<MenuItem> m_allItems = new List<MenuItem>();
        private MenuItem m_selected;
        private bool m_active;
        private float m_openedAt = -10f;

        private Canvas m_canvas;
        private Text m_titleText;
        private Text m_descText;
        private Text m_hintText;

        private float m_forestHover, m_stoneHover;
        private float m_forestCollapse, m_stoneCollapse;
        private bool m_forestExpanded, m_stoneExpanded;

        private static readonly Color GoldColor = new Color(1f, 0.83f, 0.35f, 1f);
        private static readonly Color BorderIdle = new Color(0.78f, 0.75f, 0.68f, 0.55f);
        private static readonly Color FillColor = new Color(0.04f, 0.04f, 0.04f, 0.88f);
        private static readonly Color LabelIdle = new Color(0.93f, 0.91f, 0.86f, 0.92f);
        private static readonly Color GlowIdle = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color GlowSel = new Color(1f, 0.83f, 0.35f, 0.55f);

        private static Font s_font;
        private static Sprite s_circle;
        private static Sprite s_ring;
        private static Sprite s_glow;
        private static Sprite s_vignette;

        private static Font GetFont()
        {
            if (s_font != null) return s_font;
            try { s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { try { s_font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return s_font ?? Font.CreateDynamicFontFromOSFont("Arial", 14);
        }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildUI();
            gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public static void Show(ZDOID trollID)
        {
            if (Instance == null)
            {
                GameObject go = new GameObject("TrollRadialMenuSystem");
                DontDestroyOnLoad(go);
                go.AddComponent<TrollRadialMenu>();
            }
            if (Instance == null) return;

            TrollMenuState.TrollID = trollID;
            TrollMenuState.IsOpen = true;
            Instance.m_active = true;
            Instance.m_openedAt = Time.unscaledTime;
            Instance.m_forestExpanded = Instance.m_stoneExpanded = false;
            Instance.m_forestHover = Instance.m_stoneHover = 0;
            Instance.ResetSelection();
            Instance.ApplyVisibility();
            Instance.gameObject.SetActive(true);
        }

        public static void Close()
        {
            if (Instance == null) return;
            Instance.m_active = false;
            Instance.gameObject.SetActive(false);
            TrollMenuState.IsOpen = false;
        }

        // ============================================================
        //  ПОСТРОЕНИЕ UI
        // ============================================================
        private void BuildUI()
        {
            GameObject root = new GameObject("Root");
            root.AddComponent<RectTransform>();
            root.transform.SetParent(transform, false);

            m_canvas = root.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = 500;
            root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // виньетка (прозрачный центр -> тёмные края)
            Image vig = MakeImage(root.transform, "Vignette", VignetteSprite, Color.white);
            StretchFull(vig.rectTransform);

            // центральный диск-подложка
            Image disc = MakeImage(root.transform, "Disc", CircleSprite, new Color(0.02f, 0.02f, 0.02f, 0.92f));
            Place(disc.rectTransform, Vector2.zero, new Vector2(460, 460));

            // лёгкая кромка диска
            Image discEdge = MakeImage(root.transform, "DiscEdge", RingSprite, new Color(1f, 1f, 1f, 0.12f));
            Place(discEdge.rectTransform, Vector2.zero, new Vector2(466, 466));

            // основное кольцо
            Image ring = MakeImage(root.transform, "Ring", RingSprite, new Color(1f, 1f, 1f, 0.16f));
            Place(ring.rectTransform, Vector2.zero, new Vector2(780, 780));

            // декоративное тонкое кольцо
            Image ring2 = MakeImage(root.transform, "Ring2", RingSprite, new Color(1f, 0.83f, 0.35f, 0.10f));
            Place(ring2.rectTransform, Vector2.zero, new Vector2(660, 660));

            // тексты в центре
            m_titleText = MakeText(root.transform, "Title", 27, Color.white);
            Place(m_titleText.rectTransform, new Vector2(0, 40), new Vector2(400, 38));

            m_descText = MakeText(root.transform, "Desc", 16, new Color(0.86f, 0.86f, 0.86f, 0.95f));
            Place(m_descText.rectTransform, new Vector2(0, -42), new Vector2(400, 66));

            m_hintText = MakeText(root.transform, "Hint", 13, new Color(0.7f, 0.7f, 0.7f, 0.75f));
            m_hintText.text = "ЛКМ — выбрать   •   ПКМ / Y — закрыть";
            Place(m_hintText.rectTransform, new Vector2(0, -112), new Vector2(420, 22));

            BuildItems();
        }

        private static Image MakeImage(Transform parent, string name, Sprite sprite, Color color)
        {
            GameObject go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static Text MakeText(Transform parent, string name, int size, Color color)
        {
            GameObject go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = GetFont();
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private static void Place(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        // ============================================================
        //  ПУНКТЫ
        // ============================================================
        private void BuildItems()
        {
            // главные: верх — заморозка, лево — лес, право — камень, низ — маршрут
            AddItem("Freeze", 90f, false, null,
                "Не двигаться", "Режим неподвижности: вкл/выкл",
                LoadEmbeddedIcon("freeze.png"), OnFreeze);

            AddItem("Forest", 180f, false, "forest",
                "Лес", "Наведите, чтобы выбрать древесину",
                LoadEmbeddedIcon("forest.png"), null);

            AddItem("Stone", 0f, false, "stone",
                "Камень", "Наведите, чтобы выбрать материал",
                LoadEmbeddedIcon("stone.png", "stone"), null);

            AddItem("Map", 270f, false, null,
                "Маршрут", "Отправить тролля по карте",
                LoadEmbeddedIcon("map.png"), OnMap);

            // подпункты леса
            AddItem("CoreWood", 150f, true, "forest",
                "Цельная древесина", "Сосна, пихта",
                LoadEmbeddedIcon("CoreWood.png"), () => OnGather(GatherType.CoreWood));
            AddItem("FineWood", 180f, true, "forest",
                "Качественная древесина", "Берёза, дуб",
                LoadEmbeddedIcon("fineWood.png"), () => OnGather(GatherType.FineWood));
            AddItem("Wood", 210f, true, "forest",
                "Древесина", "Бук, пни, брёвна",
                LoadEmbeddedIcon("Wood.png"), () => OnGather(GatherType.Wood));

            // подпункты камня
            AddItem("StoneRes", 20f, true, "stone",
                "Камень", "Валуны и каменные глыбы",
                LoadEmbeddedIcon("stone.png", "stone"), () => OnGather(GatherType.Stone));
            AddItem("Copper", -20f, true, "stone",
                "Медь", "Медные жилы и месторождения",
                LoadEmbeddedIcon("copper.png"), () => OnGather(GatherType.Copper));

            ApplyVisibility();
        }

        private void AddItem(string id, float angle, bool isSub, string group,
            string name, string desc, Sprite icon, Action onSelect)
        {
            MenuItem item = new MenuItem
            {
                Name = name,
                Description = desc,
                Icon = icon,
                OnSelect = onSelect,
                Angle = angle,
                IsSubItem = isSub,
                GroupId = group,
                Visible = !isSub
            };

            float btnSize = isSub ? SubButtonSize : MainButtonSize;
            Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));

            GameObject root = new GameObject("Item_" + id);
            root.AddComponent<RectTransform>();
            root.transform.SetParent(m_canvas.transform, false);
            Place(root.GetComponent<RectTransform>(), dir * ItemRadius, Vector2.zero);

            // мягкое свечение позади кнопки
            Image glow = MakeImage(root.transform, "Glow", GlowSprite, GlowIdle);
            Place(glow.rectTransform, Vector2.zero, new Vector2(btnSize * 1.6f, btnSize * 1.6f));

            // тёмная круглая подложка
            Image fill = MakeImage(root.transform, "Fill", CircleSprite, FillColor);
            Place(fill.rectTransform, Vector2.zero, new Vector2(btnSize, btnSize));

            // ободок
            Image border = MakeImage(root.transform, "Border", RingSprite, BorderIdle);
            Place(border.rectTransform, Vector2.zero, new Vector2(btnSize, btnSize));

            // иконка
            Image iconImg = MakeImage(root.transform, "Icon", icon, Color.white);
            iconImg.preserveAspect = true;
            Place(iconImg.rectTransform, Vector2.zero, new Vector2(btnSize * 0.62f, btnSize * 0.62f));

            // подпись под кнопкой
            Text label = MakeText(root.transform, "Label", isSub ? 13 : 17, LabelIdle);
            label.text = name;
            Place(label.rectTransform, new Vector2(0, -LabelOffset), new Vector2(210, 30));

            item.Root = root;
            item.GlowImage = glow;
            item.FillImage = fill;
            item.BorderImage = border;
            item.IconImage = iconImg;
            item.LabelText = label;

            m_allItems.Add(item);
        }

        // ============================================================
        //  ЛОГИКА
        // ============================================================
        private void Update()
        {
            if (!m_active) return;

            Player pl = Player.m_localPlayer;
            if (pl == null || pl.IsDead()) { Close(); return; }

            if (Time.unscaledTime - m_openedAt > OpenGrace)
            {
                if (Input.GetKeyDown(KeyCode.Y) || Input.GetKeyDown(KeyCode.Escape) ||
                    Input.GetMouseButtonDown(1))
                { Close(); return; }
            }

            Vector2 center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Vector2 diff = (Vector2)Input.mousePosition - center;

            HandleSelection(diff);
            UpdateGroupExpansion(Time.deltaTime);

            if (Input.GetMouseButtonDown(0) && m_selected != null)
                ConfirmSelection();
        }

        private void HandleSelection(Vector2 diff)
        {
            if (diff.magnitude < DeadZone)
            {
                if (m_selected != null) ResetSelection();
                return;
            }

            float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;

            MenuItem best = null;
            float bestDist = 999f;
            foreach (MenuItem item in m_allItems)
            {
                if (!item.Visible) continue;
                float d = Mathf.Abs(Mathf.DeltaAngle(angle, item.Angle));
                if (d < bestDist) { bestDist = d; best = item; }
            }

            if (best != m_selected)
            {
                if (m_selected != null) UpdateVisual(m_selected, false);
                m_selected = best;
                if (m_selected != null)
                {
                    UpdateVisual(m_selected, true);
                    UpdateCenterText(m_selected.Name, m_selected.Description);
                }
            }
        }

        private void UpdateGroupExpansion(float dt)
        {
            StepGroup("forest", ref m_forestHover, ref m_forestCollapse, ref m_forestExpanded, dt);
            StepGroup("stone", ref m_stoneHover, ref m_stoneCollapse, ref m_stoneExpanded, dt);
        }

        private void StepGroup(string groupId, ref float hover, ref float collapse, ref bool expanded, float dt)
        {
            bool hovering = m_selected != null && m_selected.GroupId == groupId &&
                (expanded || !m_selected.IsSubItem);

            if (hovering)
            {
                hover += dt; collapse = 0;
                if (hover >= ExpandDelay && !expanded)
                {
                    expanded = true;
                    ApplyVisibility();
                }
            }
            else if (expanded)
            {
                collapse += dt;
                if (collapse >= CollapseDelay)
                {
                    expanded = false; hover = 0;
                    ApplyVisibility();
                }
            }
            else hover = 0;
        }

        private void ApplyVisibility()
        {
            foreach (MenuItem item in m_allItems)
            {
                if (item.GroupId == null) continue;

                bool grpExpanded = item.GroupId == "forest" ? m_forestExpanded : m_stoneExpanded;
                item.Visible = item.IsSubItem ? grpExpanded : !grpExpanded;
            }
            RefreshRoots();
        }

        private void RefreshRoots()
        {
            foreach (MenuItem item in m_allItems)
            {
                if (item?.Root == null) continue;
                if (item.Root.activeSelf != item.Visible)
                    item.Root.SetActive(item.Visible);
            }
        }

        private void ConfirmSelection()
        {
            MenuItem sel = m_selected;

            // клик по главной кнопке группы — мгновенное раскрытие
            if (sel.GroupId != null && !sel.IsSubItem)
            {
                if (sel.GroupId == "forest") { m_forestHover = ExpandDelay + 0.01f; m_forestCollapse = 0; }
                else { m_stoneHover = ExpandDelay + 0.01f; m_stoneCollapse = 0; }
                return;
            }

            sel.OnSelect?.Invoke();
            Close();
        }

        private void ResetSelection()
        {
            if (m_selected != null) UpdateVisual(m_selected, false);
            m_selected = null;
            UpdateCenterText("", "");
        }

        private void UpdateVisual(MenuItem item, bool selected)
        {
            if (item.BorderImage != null)
                item.BorderImage.color = selected ? GoldColor : BorderIdle;
            if (item.GlowImage != null)
                item.GlowImage.color = selected ? GlowSel : GlowIdle;
            if (item.IconImage != null)
                item.IconImage.color = selected ? Color.white : new Color(1, 1, 1, 0.88f);
            if (item.LabelText != null)
                item.LabelText.color = selected ? GoldColor : LabelIdle;
            if (item.Root != null)
                item.Root.transform.localScale = Vector3.one * (selected ? 1.14f : 1f);
        }

        private void UpdateCenterText(string title, string desc)
        {
            if (string.IsNullOrEmpty(title)) title = "Выберите действие";
            if (m_titleText != null) m_titleText.text = title;
            if (m_descText != null) m_descText.text = desc;
        }

        // ============================================================
        //  ДЕЙСТВИЯ
        // ============================================================
        private void OnFreeze()
        {
            ZNetView nv = FindTrollNView();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return;

            bool current = nv.GetZDO().GetBool(TrollTamePatches.ZDO_FREEZE_KEY, false);
            nv.ClaimOwnership();
            nv.GetZDO().Set(TrollTamePatches.ZDO_FREEZE_KEY, !current);

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                !current ? "Режим неподвижности: ВКЛЮЧЕН" : "Режим неподвижности: ВЫКЛЮЧЕН", 0, null, false);
        }

        private void OnMap()
        {
            TrollMenuState.RouteBypass = true;
            try { TrollBuildingMod.TrollWalkRouteSession.Begin(TrollMenuState.TrollID); }
            finally { TrollMenuState.RouteBypass = false; }
        }

        private void OnGather(GatherType type)
        {
            ZNetView nv = FindTrollNView();
            if (nv == null) return;
            TrollGatherController.Start(nv, type);
        }

        private ZNetView FindTrollNView()
        {
            if (TrollMenuState.TrollID == ZDOID.None) return null;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(TrollMenuState.TrollID) : null;
            return go != null ? go.GetComponent<ZNetView>() : null;
        }

        // ============================================================
        //  ПРОЦЕДУРНЫЕ СПРАЙТЫ (256px, антиалиасинг)
        // ============================================================
        private static Sprite CircleSprite
        {
            get
            {
                if (s_circle != null) return s_circle;
                int size = 256;
                float c = (size - 1) * 0.5f;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                Color32[] px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) - c;
                        float a = Mathf.Clamp01(1.5f - d); // ~1.5px AA
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                s_circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
                return s_circle;
            }
        }

        private static Sprite RingSprite
        {
            get
            {
                if (s_ring != null) return s_ring;
                int size = 256;
                float c = (size - 1) * 0.5f;
                float thick = 10f; // толщина в px спрайта
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                Color32[] px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) - c;
                        float a = Mathf.Clamp01(1.5f - Mathf.Abs(d + thick * 0.5f)) +
                                  Mathf.Clamp01(1.5f - Mathf.Abs(d - thick * 0.5f));
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                s_ring = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
                return s_ring;
            }
        }

        private static Sprite GlowSprite
        {
            get
            {
                if (s_glow != null) return s_glow;
                int size = 128;
                float c = (size - 1) * 0.5f;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                Color32[] px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float r = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * a; // мягкий радиальный градиент
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                s_glow = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
                return s_glow;
            }
        }

        private static Sprite VignetteSprite
        {
            get
            {
                if (s_vignette != null) return s_vignette;
                int size = 128;
                float c = (size - 1) * 0.5f;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                Color32[] px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float r = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                        float a = Mathf.SmoothStep(0.25f, 1f, r) * 0.5f; // центр чист, края тёмные
                        px[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                s_vignette = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
                return s_vignette;
            }
        }

        private static Sprite CreateColoredIcon(Color32 color)
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Abs(x - size / 2f + 0.5f) + Mathf.Abs(y - size / 2f + 0.5f);
                    px[y * size + x] = d < size * 0.36f ? color : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // ============================================================
        //  ЗАГРУЗКА ИКОНОК — EMBEDDED RESOURCE (по суффиксу имени)
        // ============================================================
        private static Sprite LoadEmbeddedIcon(params string[] fileNames)
        {
            try
            {
                if (fileNames == null) return null;
                Assembly asm = Assembly.GetExecutingAssembly();

                foreach (string fileName in fileNames)
                {
                    if (string.IsNullOrEmpty(fileName)) continue;

                    string best = null;
                    foreach (string res in asm.GetManifestResourceNames())
                        if (res.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
                            (best == null || res.Length < best.Length))
                            best = res;

                    if (best == null) continue;

                    using (Stream stream = asm.GetManifestResourceStream(best))
                    {
                        if (stream == null) continue;
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!tex.LoadImage(bytes)) continue;

                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;

                        try
                        {
                            Color32[] pixels = tex.GetPixels32();
                            bool dirty = false;
                            for (int i = 0; i < pixels.Length; i++)
                                if (pixels[i].a > 0 && pixels[i].a < 15)
                                { pixels[i] = new Color32(0, 0, 0, 0); dirty = true; }
                            if (dirty) { tex.SetPixels32(pixels); tex.Apply(false); }
                        }
                        catch { }

                        Debug.Log("[TrollMenu] Embedded icon: " + best);
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    }
                }

                Debug.LogWarning("[TrollMenu] Embedded icon NOT found: " + string.Join("/", fileNames) +
                    " | available: " + string.Join(", ", asm.GetManifestResourceNames()));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollMenu] Embedded icon load failed: " + e.Message);
            }
            return CreateColoredIcon(ColorForName(fileNames != null && fileNames.Length > 0 ? fileNames[0] : ""));
        }

        private static Color32 ColorForName(string name)
        {
            if (name == null) return new Color32(120, 120, 120, 255);
            string n = name.ToLower();
            if (n.Contains("forest")) return new Color32(70, 140, 70, 255);
            if (n.Contains("map")) return new Color32(200, 170, 60, 255);
            if (n.Contains("copper")) return new Color32(190, 110, 60, 255);
            if (n.Contains("stone")) return new Color32(140, 140, 140, 255);
            if (n.Contains("core")) return new Color32(150, 110, 60, 255);
            if (n.Contains("fine")) return new Color32(200, 190, 140, 255);
            if (n.Contains("wood")) return new Color32(160, 120, 70, 255);
            return new Color32(120, 120, 120, 255);
        }
    }

    // ================================================================
    //  ПАТЧИ
    // ================================================================
    [HarmonyPatch]
    public static class MenuPatches
    {
        // Y открывает меню вместо прямого старта маршрута
        [HarmonyPatch(typeof(TrollBuildingMod.TrollWalkRouteSession), "Begin")]
        [HarmonyPrefix]
        private static bool RouteBegin_Prefix(ZDOID troll)
        {
            if (TrollMenuState.RouteBypass) return true;

            if (TrollRadialMenu.IsOpen) { TrollRadialMenu.Close(); return false; }

            TrollRadialMenu.Show(troll);
            return false;
        }

        // Игровой ввод заблокирован, пока меню открыто
        [HarmonyPatch(typeof(Player), "TakeInput")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        private static bool Player_TakeInput_Prefix(ref bool __result)
        {
            if (TrollMenuState.IsOpen) { __result = false; return false; }
            return true;
        }

        // Курсор свободен, пока меню открыто (та же ветка, что ваниль
        // выполняет для Hud.InRadial() с активной мышью)
        [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        private static bool GameCamera_UpdateMouseCapture_Prefix(GameCamera __instance)
        {
            if (!TrollMenuState.IsOpen) return true;

            try
            {
                ZCursor.LockState = CursorLockMode.None;
                ZCursor.Show();
            }
            catch { }
            return false; // не даём игре залочить курсор
        }

        // Обзор заморожен (мышь)
        [HarmonyPatch(typeof(ZInput), "GetMouseDelta")]
        [HarmonyPrefix]
        private static bool ZInput_GetMouseDelta_Prefix(ref Vector2 __result)
        {
            if (TrollMenuState.IsOpen) { __result = Vector2.zero; return false; }
            return true;
        }

        // Обзор заморожен (геймпад)
        [HarmonyPatch(typeof(ZInput), "GetJoyRightStick")]
        [HarmonyPrefix]
        private static bool ZInput_GetJoyRightStick_Prefix(ref Vector2 __result)
        {
            if (TrollMenuState.IsOpen) { __result = Vector2.zero; return false; }
            return true;
        }

        // Камера (включая скролл-зум) заморожена, пока меню открыто
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        [HarmonyPrefix]
        private static bool GameCamera_UpdateCamera_Prefix()
        {
            return !TrollMenuState.IsOpen;
        }

        // Idle-движение отключено во время добычи
        [HarmonyPatch(typeof(BaseAI), "IdleMovement")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool Menu_IdleMovement_Prefix(BaseAI __instance)
        {
            if (__instance == null) return true;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase)) return true;

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return true;

            if (nv.GetZDO().GetInt(TrollGatherController.HashGatherKey, 0) > 0) return false;
            return true;
        }

        // Контроллер маршрута отключён во время добычи
        [HarmonyPatch(typeof(TrollBuildingMod.TrollWalkController), "TickAI")]
        [HarmonyPrefix]
        private static bool WalkController_TickAI_Prefix(TrollBuildingMod.TrollWalkController __instance)
        {
            ZNetView nv = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            if (nv != null && nv.IsValid() && nv.GetZDO() != null &&
                nv.GetZDO().GetInt(TrollGatherController.HashGatherKey, 0) > 0)
                return false;
            return true;
        }

        // Восстановление добычи после перезахода
        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Character_Awake_GatherRestore(Character __instance)
        {
            if (__instance == null) return;
            if (!__instance.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase)) return;

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || nv.GetZDO() == null) return;

            if (nv.GetZDO().GetInt(TrollGatherController.HashGatherKey, 0) > 0 &&
                __instance.GetComponent<TrollGatherController>() == null)
            {
                __instance.gameObject.AddComponent<TrollGatherController>();
            }
        }
    }
}