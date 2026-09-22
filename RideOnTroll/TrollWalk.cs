using GUIFramework;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TrollTamerMod;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace TrollBuildingMod
{
    #region Константы / локализация / иконки

    public static class TrollWalkConstants
    {
        public const string KeyActive = "TrollWalk_Active";
        public const string KeyTarget = "TrollWalk_Target";
        public const string KeyName = "TrollWalk_Name";
        public const string KeyLastPos = "TrollWalk_LastPos";
        public const string KeyLastUpdate = "TrollWalk_LastUpdate";
        public const string KeySpeed = "TrollWalk_Speed";
        public const string KeyArrivedFlag = "TrollWalk_Arrived";

        public static readonly int HashActive = KeyActive.GetStableHashCode();
        public static readonly int HashTarget = KeyTarget.GetStableHashCode();
        public static readonly int HashName = KeyName.GetStableHashCode();
        public static readonly int HashLastPos = KeyLastPos.GetStableHashCode();
        public static readonly int HashLastUpdate = KeyLastUpdate.GetStableHashCode();
        public static readonly int HashSpeed = KeySpeed.GetStableHashCode();
        public static readonly int HashArrivedFlag = KeyArrivedFlag.GetStableHashCode();

        public const float ArriveRadius = 3f;       // «пришёл»
        public const float RunDistance = 15f;       // дальше — бежит
        public const float MinSpeed = 2.5f;         // пол скорости для карты/виртуального ходока
        public const float DefaultSpeed = 4f;       // м/с до первого замера
        public const float VirtualRunSpeed = 5f;    // минимум скорости виртуально при беговой дистанции
        public const float TelemetryInterval = 2f;   // владелец пишет LastPos/Speed
        public const float VirtualStepInterval = 2f; // «виртуальный ходок» вне зоны
        public const float WaypointStep = 40f;       // промежуточная точка при цели вне зоны
        public const float FrozenWatchdogSeconds = 6f; // вотчдог «GO замер вне зоны»

        public static readonly string[] TrollPrefabNames = { "Troll", "Troll_Log" };

        public static bool IsDedicatedServer => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    public static class TrollWalkLoc
    {
        public static string T(string en, string ru)
        {
            string lang = Localization.instance != null
                ? Localization.instance.GetSelectedLanguage()
                : PlayerPrefs.GetString("language", "English");
            return (lang != null && lang.IndexOf("Russian", StringComparison.OrdinalIgnoreCase) >= 0) ? ru : en;
        }
    }

    public static class TrollWalkIcons
    {
        public static Sprite Road;
        public static Sprite Troll;
        private static bool s_loaded;

        public static void EnsureLoaded()
        {
            if (s_loaded || TrollWalkConstants.IsDedicatedServer) return;
            s_loaded = true;
            Road = Load("road.png", new Color32(230, 160, 40, 255));
            Troll = Load("troll.png", new Color32(90, 170, 90, 255));
        }

        private static Sprite Load(string file, Color32 fallbackColor)
        {
            Sprite s = LoadFromDisk(file);
            if (s != null) return s;

            s = LoadFromResources(file);
            if (s != null) return s;

            Debug.LogWarning($"[TrollWalk] Icon '{file}' not found on disk or in resources. Using fallback.");
            Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[32 * 32];
            for (int i = 0; i < px.Length; i++) px[i] = fallbackColor;
            t.SetPixels32(px);
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite LoadFromDisk(string file)
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string[] paths =
                {
                    Path.Combine(asmDir, "icon", file),
                    Path.Combine(asmDir, file),
                    Path.Combine(Directory.GetCurrentDirectory(), "icon", file)
                };
                foreach (string path in paths)
                {
                    if (!File.Exists(path)) continue;
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(File.ReadAllBytes(path)))
                    {
                        Debug.Log($"[TrollWalk] Icon '{file}' loaded from disk: {path}");
                        return PostProcess(tex);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[TrollWalk] Disk icon load {file}: {e.Message}"); }
            return null;
        }

        // Внедрённые ресурсы сборки (Build Action = Embedded Resource)
        private static Sprite LoadFromResources(string file)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames();
                foreach (string res in names)
                {
                    if (!res.EndsWith(file, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream stream = asm.GetManifestResourceStream(res))
                    {
                        if (stream == null) continue;
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(bytes))
                        {
                            Debug.Log($"[TrollWalk] Icon '{file}' loaded from embedded resource '{res}'");
                            return PostProcess(tex);
                        }
                    }
                }
                if (names.Length > 0)
                    Debug.LogWarning($"[TrollWalk] '{file}' not among {names.Length} resources. Sample: {string.Join(", ", names.Take(6).ToArray())}");
                else
                    Debug.LogWarning("[TrollWalk] Assembly has NO embedded resources. Check csproj: <EmbeddedResource Include=\"icon\\" + file + "\" />");
            }
            catch (Exception e) { Debug.LogWarning($"[TrollWalk] Resource icon load {file}: {e.Message}"); }
            return null;
        }

        private static Sprite PostProcess(Texture2D tex)
        {
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            try
            {
                Color32[] pixels = tex.GetPixels32();
                bool dirty = false;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a > 0 && pixels[i].a < 15)
                    {
                        pixels[i] = new Color32(0, 0, 0, 0);
                        dirty = true;
                    }
                }
                if (dirty) { tex.SetPixels32(pixels); tex.Apply(false); }
            }
            catch { }

            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }
    }

    #endregion

    #region Контроллер тролля

    public class TrollWalkController : MonoBehaviour
    {
        private Character m_character;
        private ZNetView m_nview;
        private MonsterAI m_ai;

        // MoveTo — protected в BaseAI, вызываем рефлексией
        private static readonly MethodInfo s_moveTo = AccessTools.Method(typeof(BaseAI), "MoveTo");

        private float m_telemetryTimer;
        private Vector3 m_telLastPos;

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_nview = GetComponent<ZNetView>();
            m_ai = GetComponent<MonsterAI>();
            m_telLastPos = transform.position;
            if (m_character != null)
            {
                try { m_character.m_onDeath = (Action)Delegate.Combine(m_character.m_onDeath, new Action(OnDeath)); }
                catch { }
            }
        }

        private void Start()
        {
            try { CatchUpOnLoad(); } catch { }
        }

        // Компенсация «шёл, пока никто не видел»: доигрываем по расписанию
        private void CatchUpOnLoad()
        {
            if (m_nview == null || !m_nview.IsValid()) return;
            ZDO zdo = m_nview.GetZDO();
            if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false)) return;

            if (!zdo.HasOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
            if (!zdo.IsOwner()) return; // владелец где-то есть — он и разрулит

            long tick = zdo.GetLong(TrollWalkConstants.HashLastUpdate, 0L);
            double elapsed = tick > 0 ? Math.Max(0.0, (ZNet.instance.GetTime() - new DateTime(tick)).TotalSeconds) : 0;
            if (elapsed < 8.0) return;

            Vector3 lastPos = zdo.GetVec3(TrollWalkConstants.HashLastPos, transform.position);
            Vector3 target = zdo.GetVec3(TrollWalkConstants.HashTarget, lastPos);
            float dist = Utils.DistanceXZ(target, lastPos);
            float speed = Mathf.Max(TrollWalkConstants.MinSpeed, zdo.GetFloat(TrollWalkConstants.HashSpeed, TrollWalkConstants.DefaultSpeed));
            float travel = Mathf.Min(speed * (float)elapsed, dist);

            if (dist - travel <= TrollWalkConstants.ArriveRadius)
            {
                zdo.Set(TrollWalkConstants.HashActive, false);
                zdo.Set(TrollWalkConstants.HashArrivedFlag, true);
                return;
            }
            if (dist > 0.01f)
            {
                Vector3 dir = new Vector3((target.x - lastPos.x) / dist, 0f, (target.z - lastPos.z) / dist);
                Vector3 est = lastPos + dir * travel;
                if (Utils.DistanceXZ(transform.position, est) > 8f)
                    transform.position = new Vector3(est.x, transform.position.y, est.z);
            }
        }

        // Вызывается постфиксом MonsterAI.UpdateAI (только у владельца — проверка внутри).
        //
        // Цель В активной зоне  -> ванильный MoveTo (полный поиск пути).
        // Цель ВНЕ активной зоны -> промежуточные точки внутри зоны; на границе —
        //   прямо к цели. Держателем владения (менеджер) тролль идёт и ВНЕ зоны —
        //   до края секторов симуляции, где ваниль сама уберёт GO и виртуальный
        //   ходок поедет дальше без швов.
        public void TickAI(float dt)
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner()) return;
            if (m_character == null || m_character.IsDead() || m_ai == null) return;
            if (s_moveTo == null) return;

            ZDO zdo = m_nview.GetZDO();
            if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false)) return;
            if (TrollTamePatches.IsFrozenTroll(m_character)) return;
            if (m_ai.IsSleeping()) return;

            // Бой: ваниль сама ведёт тролля к атакующему
            if (m_ai.GetTargetCreature() != null || m_ai.GetStaticTarget() != null) return;
            if (m_character.InAttack() || m_character.IsStaggering()) return;

            Vector3 target = zdo.GetVec3(TrollWalkConstants.HashTarget, transform.position);
            float dist = Utils.DistanceXZ(target, transform.position);
            bool run = dist > TrollWalkConstants.RunDistance;

            if (dist <= TrollWalkConstants.ArriveRadius)
            {
                m_ai.StopMoving();
                FinishRoute(zdo);
                return;
            }

            bool targetInZone = ZNetScene.InActiveArea(target, ZNet.instance.GetReferencePosition());

            if (targetInZone)
            {
                s_moveTo.Invoke(m_ai, new object[] { dt, target, TrollWalkConstants.ArriveRadius, run });
                return;
            }

            // Цель вне зоны: идём к промежуточной точке
            Vector3 pos = transform.position;
            Vector3 dir = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();

            float step = TrollWalkConstants.WaypointStep;
            while (step > 4f && !ZNetScene.InActiveArea(pos + dir * step, ZNet.instance.GetReferencePosition()))
                step *= 0.5f;
            step = Mathf.Min(step, dist - TrollWalkConstants.ArriveRadius * 0.5f);
            if (step < 1f)
            {
                // тролль за границей активной зоны (владение удерживает менеджер):
                // идём прямо — террейн в полосе секторов симуляции загружен
                m_ai.MoveTowards(dir, run);
                return;
            }

            s_moveTo.Invoke(m_ai, new object[] { dt, pos + dir * step, 0.5f, run });
        }

        private void FinishRoute(ZDO zdo, bool cancelled = false)
        {
            if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.Set(TrollWalkConstants.HashActive, false);
            zdo.Set(TrollWalkConstants.HashArrivedFlag, !cancelled);

            try
            {
                if (m_ai != null) m_ai.ResetPatrolPoint();
                zdo.Set(ZDOVars.s_patrol, false);
            }
            catch { }

            TrollWalkManager.Instance?.UpdatePins();
        }

        private void OnDeath()
        {
            try
            {
                if (m_nview != null && m_nview.IsValid())
                {
                    ZDO zdo = m_nview.GetZDO();
                    if (zdo != null)
                    {
                        if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
                        zdo.Set(TrollWalkConstants.HashActive, false);
                    }
                }
            }
            catch { }
        }

        private void FixedUpdate()
        {
            try { Telemetry(); } catch { }
        }

        // Владелец пишет в ZDO: где тролль, когда, с какой скоростью (для карты вне зоны)
        private void Telemetry()
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner()) return;
            ZDO zdo = m_nview.GetZDO();
            if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false)) return;

            m_telemetryTimer += Time.fixedDeltaTime;
            if (m_telemetryTimer < TrollWalkConstants.TelemetryInterval) return;

            float d = Utils.DistanceXZ(transform.position, m_telLastPos);
            float inst = d / Mathf.Max(0.1f, m_telemetryTimer);
            float prev = zdo.GetFloat(TrollWalkConstants.HashSpeed, TrollWalkConstants.DefaultSpeed);

            float speed;
            if (inst >= TrollWalkConstants.MinSpeed)
                speed = Mathf.Clamp(Mathf.Lerp(prev, inst, 0.5f), TrollWalkConstants.MinSpeed, 8f);
            else
                speed = Mathf.Max(prev, TrollWalkConstants.MinSpeed);

            zdo.Set(TrollWalkConstants.HashLastPos, transform.position);
            zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks);
            zdo.Set(TrollWalkConstants.HashSpeed, speed);

            m_telemetryTimer = 0f;
            m_telLastPos = transform.position;

            TrollWalkManager.Instance?.NotifyActive(zdo.m_uid);
        }
    }

    #endregion

    #region Менеджер: маршруты, пины, владение, виртуальный ходок

    public class TrollWalkManager : MonoBehaviour
    {
        public static TrollWalkManager Instance;
        internal static bool s_internalRemove;

        // текущая боевая цель MonsterAI — сбрасывается при отправке в путь
        private static readonly AccessTools.FieldRef<MonsterAI, Character> s_targetCreatureRef =
            AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly MethodInfo s_wakeupMethod =
            AccessTools.Method(typeof(MonsterAI), "Wakeup");
        // словарь инстансов ZNetScene — аккуратное убирание GO без уничтожения ZDO
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instancesField =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        internal class RouteInfo
        {
            public ZDOID Troll;
            public Minimap.PinData RoutePin;
            public Minimap.PinData TrackerPin;
            // вотчдог «GO замер вне активной зоны»
            public bool ProgressInit;
            public Vector3 ProgressPos;
            public float ProgressTime;
            // виртуальный режим (GO убран, ездит ZDO)
            public bool VirtualMode;
        }

        private readonly Dictionary<ZDOID, RouteInfo> s_routes = new Dictionary<ZDOID, RouteInfo>();
        internal List<ZDOID> ActiveRouteIds
        {
            get
            {
                List<ZDOID> list = new List<ZDOID>(s_routes.Count);
                foreach (KeyValuePair<ZDOID, RouteInfo> kv in s_routes)
                {
                    ZDO z = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(kv.Key) : null;
                    if (z != null && z.GetBool(TrollWalkConstants.HashActive, false)) list.Add(kv.Key);
                }
                return list;
            }
        }

        private float m_virtualTimer;
        private int m_scanPrefab;
        private int m_scanIndex;
        private readonly List<ZDO> m_scanBuf = new List<ZDO>();
        private bool m_scanProcessing;
        private static float s_lastHint = -10f;

        private static readonly MethodInfo s_addPin = AccessTools.Method(typeof(Minimap), "AddPin");
        private static readonly MethodInfo s_removePin = AccessTools.Method(typeof(Minimap), "RemovePin",
            new[] { typeof(Minimap.PinData) });
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_pinsField =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdateField =
            AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
        private static object s_puidDefault;

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; }

        public static void EnsureCreated()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("TrollWalkManager");
            DontDestroyOnLoad(go);
            go.AddComponent<TrollWalkManager>();
            Debug.Log("[TrollWalk] Manager created");
        }

        private void Update()
        {
            if (ZNetScene.instance == null || ZNet.instance == null || ZDOMan.instance == null)
            {
                HardReset();
                return;
            }

            try { KeepOwnershipOfTravelingTrolls(); } catch { }

            if (ZNet.instance.IsServer())
            {
                m_virtualTimer += Time.deltaTime;
                if (m_virtualTimer >= TrollWalkConstants.VirtualStepInterval)
                {
                    m_virtualTimer = 0f;
                    try { VirtualStep(); } catch { }
                }
            }
            BackgroundScan();
        }

        private void HardReset()
        {
            if (s_routes.Count == 0 && !TrollWalkRouteSession.Active) return;
            Minimap mm = Minimap.instance;
            foreach (RouteInfo ri in s_routes.Values) RemoveRoutePins(mm, ri);
            s_routes.Clear();
            TrollWalkRouteSession.HardReset();
        }

        // =====================================================================
        // УДЕРЖАНИЕ ВЛАДЕНИЯ (каждый кадр).
        //
        // КОРЕНЬ БАГА «резко останавливается»: ваниль (ZDOMan.ReleaseNearbyZDOS,
        // каждые 2 с) отбирает владение у ZDO, вышедшего из АКТИВНОЙ зоны
        // (~96 м), хотя GO тролля ещё жив до границы секторов симуляции
        // (~150 м). Без владения BaseAI.UpdateAI замирает — тролль стоит.
        // Здесь мы возвращаем владение на следующем же кадре: AI продолжает
        // работать и тролль САМ идёт через эту полосу до края секторов, где
        // ваниль сама уберёт GO (RemoveObjects) — виртуальный ходок подхватит
        // бесшовно, телеметрия пишет позицию каждые 2 с.
        // Клэимим только бесхозные (owner == 0) — за живым владельцем не лезем.
        // =====================================================================
        private void KeepOwnershipOfTravelingTrolls()
        {
            if (s_routes.Count == 0) return;

            Vector3 refPos = ZNet.instance.GetReferencePosition();
            foreach (KeyValuePair<ZDOID, RouteInfo> kv in s_routes)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(kv.Key);
                if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false)) continue;
                if (zdo.IsOwner()) continue;

                ZNetView inst = ZNetScene.instance.FindInstance(zdo);
                if (inst == null) continue;                 // GO нет — VirtualStep сам клэмит
                if (kv.Value.VirtualMode) continue;         // виртуальный режим — GO снесёт VirtualStep
                if (ZNetScene.InActiveArea(inst.transform.position, refPos)) continue; // в зоне — ваниль сама владеет

                long owner = zdo.GetOwner();
                if (owner != 0L) continue;                  // есть владелец (пусть и офлайн) — не воюем
                zdo.SetOwner(ZDOMan.GetSessionID());
            }
        }

        // =====================================================================
        // ВИРТУАЛЬНЫЙ ХОДОК (сервер/одиночка, раз в 2 с).
        //
        //  - GO в активной зоне            -> реальная симуляция, не мешаем.
        //  - GO вне активной зоны, идёт    -> не мешаем (владение удержали).
        //  - GO вне активной зоны, замер N c (AI не работает) -> вотчдог:
        //       постройки  -> ждёт игрока (GO с детьми-пинами рвать нельзя);
        //       без построек -> хендовер: GO убираем как ванильная выгрузка
        //          зоны (ZDO живёт), ZDO едет сам.
        //  - GO нет                        -> чистое виртуальное движение.
        //  - Виртуальный режим: ваниль пересоздаёт замерший GO каждые 0.03 с,
        //    пока ZDO в полосе секторов — сносим сразу (без вотчдога).
        // =====================================================================
        private void VirtualStep()
        {
            List<ZDOID> ids = s_routes.Keys.ToList();
            foreach (ZDOID id in ids)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) continue;

                if (!zdo.GetBool(TrollWalkConstants.HashActive, false))
                {
                    if (zdo.IsOwner() && zdo.GetBool(TrollWalkConstants.HashArrivedFlag, false))
                        zdo.Set(TrollWalkConstants.HashArrivedFlag, false);
                    s_routes.Remove(id);
                    continue;
                }

                if (!s_routes.TryGetValue(id, out RouteInfo ri)) { NotifyActive(id); continue; }

                // референс зоны: владелец-пир (если онлайн) либо сам сервер
                Vector3 refPos = ZNet.instance.GetReferencePosition();
                long owner = zdo.GetOwner();
                if (owner != 0L && owner != ZDOMan.GetSessionID())
                {
                    ZNetPeer peer = ZNet.instance.GetPeer(owner);
                    if (peer != null) refPos = peer.GetRefPos();
                }

                ZNetView inst = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                if (inst != null)
                {
                    // 1) GO в активной зоне — реальная симуляция контроллером
                    if (ZNetScene.InActiveArea(inst.transform.position, refPos))
                    {
                        ri.VirtualMode = false;
                        ri.ProgressInit = false;
                        continue;
                    }

                    TrollPiecesContainer cont = inst.GetComponent<TrollPiecesContainer>();
                    bool hasStructures = cont != null && cont.PieceCount > 0;

                    // 2) виртуальный режим: пересозданный ванилью замерший GO —
                    //    сносим сразу и продолжаем виртуальное движение
                    if (ri.VirtualMode)
                    {
                        if (hasStructures)
                        {
                            ri.VirtualMode = false; // защита от невозможного — назад к реальному режиму
                            continue;
                        }
                        DespawnTrollGO(zdo, inst);
                        // падаем ниже — в виртуальное движение этим же шагом
                    }
                    else
                    {
                        // 3) GO вне активной зоны: владение мы удерживаем —
                        //    если AI работает, тролль идёт сам. Следим за прогрессом.
                        Vector3 goPos = inst.transform.position;
                        if (!ri.ProgressInit)
                        {
                            ri.ProgressInit = true;
                            ri.ProgressPos = goPos;
                            ri.ProgressTime = Time.time;
                            continue;
                        }

                        if (Utils.DistanceXZ(goPos, ri.ProgressPos) > 1f)
                        {
                            // идёт сам — не мешаем
                            ri.ProgressPos = goPos;
                            ri.ProgressTime = Time.time;
                            continue;
                        }

                        if (Time.time - ri.ProgressTime < TrollWalkConstants.FrozenWatchdogSeconds)
                            continue; // ещё ждём

                        if (hasStructures)
                            continue; // постройки: GO не убираем, тролль ждёт игрока на границе

                        // 4) AI заморожен — хендовер виртуальному ходоку
                        zdo.Set(TrollWalkConstants.HashLastPos, goPos);
                        zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks);
                        DespawnTrollGO(zdo, inst);
                        ri.VirtualMode = true;
                        Debug.Log("[TrollWalk] Frozen troll GO handed over to virtual travel");
                        // падаем ниже — в виртуальное движение
                    }
                }

                if (!zdo.IsOwner())
                {
                    long o = zdo.GetOwner();
                    if (o != 0L && ZNet.instance.GetPeer(o) != null) continue;
                    zdo.SetOwner(ZDOMan.GetSessionID());
                }

                long tick = zdo.GetLong(TrollWalkConstants.HashLastUpdate, 0L);
                if (tick == 0L) { zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks); continue; }
                double elapsed = (ZNet.instance.GetTime() - new DateTime(tick)).TotalSeconds;
                if (elapsed <= 0.0) continue;

                Vector3 pos = zdo.GetVec3(TrollWalkConstants.HashLastPos, zdo.GetPosition());
                Vector3 target = zdo.GetVec3(TrollWalkConstants.HashTarget, pos);
                float dist = Utils.DistanceXZ(target, pos);
                float speed = Mathf.Max(TrollWalkConstants.MinSpeed,
                    zdo.GetFloat(TrollWalkConstants.HashSpeed, TrollWalkConstants.DefaultSpeed));
                if (dist > TrollWalkConstants.RunDistance)
                    speed = Mathf.Max(speed, TrollWalkConstants.VirtualRunSpeed);
                float travel = Mathf.Min(speed * (float)elapsed, dist);

                if (dist - travel <= TrollWalkConstants.ArriveRadius)
                {
                    Vector3 fin = new Vector3(target.x, pos.y, target.z);
                    zdo.Set(TrollWalkConstants.HashActive, false);
                    zdo.Set(TrollWalkConstants.HashArrivedFlag, true); // уведомление всем клиентам
                    zdo.SetPosition(fin);
                    zdo.Set(TrollWalkConstants.HashLastPos, fin);
                    zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks);
                }
                else if (dist > 0.01f)
                {
                    Vector3 dir = new Vector3((target.x - pos.x) / dist, 0f, (target.z - pos.z) / dist);
                    Vector3 newPos = pos + dir * travel;
                    zdo.SetPosition(newPos);
                    zdo.Set(TrollWalkConstants.HashLastPos, newPos);
                    zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks);
                }
            }
        }

        // Аккуратно убираем GO тролля, СОХРАНЯЯ ZDO — точная копия того,
        // что делает ванильная выгрузка зоны (RemoveObjects). ZNetScene.Destroy
        // использовать нельзя — он уничтожает ZDO (механика деспавна мобов).
        private static void DespawnTrollGO(ZDO zdo, ZNetView nv)
        {
            try
            {
                Dictionary<ZDO, ZNetView> instances = s_instancesField(ZNetScene.instance);
                if (instances != null) instances.Remove(zdo);
                nv.ResetZDO(); // Created=false, m_zdo=null — ZDO остаётся жить
                UnityEngine.Object.Destroy(nv.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TrollWalk] Despawn failed: {e.Message}");
            }
        }

        // Фоновый поиск троллей с активными маршрутами (по ZDO, без GO)
        private void BackgroundScan()
        {
            if (m_scanProcessing)
            {
                ProcessScan();
                return;
            }
            if (m_scanPrefab >= TrollWalkConstants.TrollPrefabNames.Length) return;

            bool done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(
                TrollWalkConstants.TrollPrefabNames[m_scanPrefab], m_scanBuf, ref m_scanIndex);
            if (done)
            {
                m_scanPrefab++;
                m_scanIndex = 0;
                if (m_scanPrefab >= TrollWalkConstants.TrollPrefabNames.Length)
                    m_scanProcessing = true;
            }
        }

        private void ProcessScan()
        {
            m_scanProcessing = false;
            foreach (ZDO zdo in m_scanBuf)
            {
                if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false)) continue;
                NotifyActive(zdo.m_uid);
            }
            m_scanBuf.Clear();
            m_scanPrefab = 0;
            m_scanIndex = 0;
        }

        internal void NotifyActive(ZDOID id)
        {
            if (id == ZDOID.None || s_routes.ContainsKey(id)) return;
            s_routes[id] = new RouteInfo { Troll = id };
        }

        public void Dispatch(ZDOID trollId, Vector3 target, string name)
        {
            if (ZDOMan.instance == null) return;
            ZDO zdo = ZDOMan.instance.GetZDO(trollId);
            if (zdo == null) return;

            ZNetView inst = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;

            // не отправляем мёртвого
            if (inst != null)
            {
                Character c = inst.GetComponent<Character>();
                if (c != null && c.IsDead())
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        TrollWalkLoc.T("The troll is dead", "Тролль мёртв"), 0, null, false);
                    return;
                }
            }

            // Снимаем ВСЕ «якоря» и отвлечения (patrol/follow/random/цель/сон)
            try
            {
                MonsterAI ai = inst != null ? inst.GetComponent<MonsterAI>() : null;
                if (ai != null)
                {
                    ai.ResetPatrolPoint();
                    ai.SetFollowTarget(null);
                    ai.ResetRandomMovement();
                    s_targetCreatureRef(ai) = null;
                    s_wakeupMethod?.Invoke(ai, null);
                }
                zdo.Set(ZDOVars.s_patrol, false);
                zdo.Set(ZDOVars.s_follow, "");
            }
            catch { }

            zdo.SetOwner(ZDOMan.GetSessionID());

            if (zdo.GetBool(TrollTamePatches.ZDO_FREEZE_KEY, false))
            {
                zdo.Set(TrollTamePatches.ZDO_FREEZE_KEY, false);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    TrollWalkLoc.T("Freeze removed — troll is ready to go", "Заморозка снята — тролль готов в путь"), 0, null, false);
            }

            zdo.Set(TrollWalkConstants.HashActive, true);
            zdo.Set(TrollWalkConstants.HashArrivedFlag, false);
            zdo.Set(TrollWalkConstants.HashTarget, target);
            zdo.Set(TrollWalkConstants.HashName, name);
            zdo.Set(TrollWalkConstants.HashLastPos, zdo.GetPosition());
            zdo.Set(TrollWalkConstants.HashLastUpdate, ZNet.instance.GetTime().Ticks);

            if (zdo.GetFloat(TrollWalkConstants.HashSpeed, 0f) < TrollWalkConstants.MinSpeed)
                zdo.Set(TrollWalkConstants.HashSpeed, TrollWalkConstants.DefaultSpeed);

            NotifyActive(trollId);
            UpdatePins();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                TrollWalkLoc.T($"{name}: on the way", $"{name}: тролль отправлен в путь"), 0, null, false);
            Debug.Log($"[TrollWalk] Dispatch troll {trollId} -> {target} '{name}'");
        }

        public void CancelRoute(ZDOID trollId, bool notify)
        {
            if (ZDOMan.instance == null) return;
            ZDO zdo = ZDOMan.instance.GetZDO(trollId);
            if (zdo != null)
            {
                if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.Set(TrollWalkConstants.HashActive, false);
                zdo.Set(TrollWalkConstants.HashArrivedFlag, false);
            }
            if (s_routes.TryGetValue(trollId, out RouteInfo ri))
            {
                RemoveRoutePins(Minimap.instance, ri);
                s_routes.Remove(trollId);
            }
            if (notify)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    TrollWalkLoc.T("Route cancelled", "Путь отменён"), 0, null, false);
            UpdatePins();
        }

        internal ZDOID FindTrollByPin(Minimap.PinData pin)
        {
            foreach (KeyValuePair<ZDOID, RouteInfo> kv in s_routes)
            {
                if (kv.Value.RoutePin == pin || kv.Value.TrackerPin == pin) return kv.Key;
            }
            return ZDOID.None;
        }

        public void UpdatePins()
        {
            if (TrollWalkConstants.IsDedicatedServer) return;
            Minimap mm = Minimap.instance;
            if (mm == null || ZDOMan.instance == null) return;
            TrollWalkIcons.EnsureLoaded();

            bool visible = TrollWalkRouteSession.PinsVisible;
            bool changed = false;
            List<ZDOID> drop = null;

            foreach (KeyValuePair<ZDOID, RouteInfo> kv in s_routes.ToList())
            {
                ZDO zdo = ZDOMan.instance.GetZDO(kv.Key);

                // Уведомление о прибытии ЛЮБЫМ способом — один раз на клиенте
                if (zdo != null && zdo.GetBool(TrollWalkConstants.HashArrivedFlag, false))
                {
                    string an = zdo.GetString(TrollWalkConstants.HashName, "");
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        TrollWalkLoc.T($"{an} has arrived", $"{an} прибыл в точку назначения"), 0, null, false);
                    zdo.Set(TrollWalkConstants.HashArrivedFlag, false);
                }

                if (zdo == null || !zdo.GetBool(TrollWalkConstants.HashActive, false))
                {
                    RemoveRoutePins(mm, kv.Value);
                    (drop ??= new List<ZDOID>()).Add(kv.Key);
                    changed = true;
                    continue;
                }

                RouteInfo ri = kv.Value;

                // ПКМ-фильтр по кнопке пина пути
                if (!visible)
                {
                    if (ri.RoutePin != null || ri.TrackerPin != null)
                    {
                        RemoveRoutePins(mm, ri);
                        changed = true;
                    }
                    continue;
                }

                string name = zdo.GetString(TrollWalkConstants.HashName, "");
                Vector3 target = zdo.GetVec3(TrollWalkConstants.HashTarget, Vector3.zero);
                Vector3 pos = EstimatePosition(zdo);

                if (ri.RoutePin == null)
                {
                    ri.RoutePin = AddPinSafe(mm, target, name, TrollWalkIcons.Road);
                    changed = true;
                }
                else
                {
                    if ((ri.RoutePin.m_pos - target).sqrMagnitude > 0.01f) { ri.RoutePin.m_pos = target; changed = true; }
                    if (ri.RoutePin.m_name != name) { ri.RoutePin.m_name = name; changed = true; }
                }

                if (ri.TrackerPin == null)
                {
                    ri.TrackerPin = AddPinSafe(mm, pos, name, TrollWalkIcons.Troll);
                    changed = true;
                }
                else if ((ri.TrackerPin.m_pos - pos).sqrMagnitude > 0.04f)
                {
                    ri.TrackerPin.m_pos = pos;
                    changed = true;
                }
                if (ri.TrackerPin.m_name != name) { ri.TrackerPin.m_name = name; changed = true; }
            }

            if (drop != null)
                foreach (ZDOID d in drop) s_routes.Remove(d);

            if (changed) SetPinUpdateRequired(mm, true);
        }

        internal Vector3 EstimatePosition(ZDO zdo)
        {
            ZNetView inst = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (inst != null) return inst.transform.position;

            Vector3 lastPos = zdo.GetVec3(TrollWalkConstants.HashLastPos, zdo.GetPosition());
            Vector3 target = zdo.GetVec3(TrollWalkConstants.HashTarget, lastPos);
            long tick = zdo.GetLong(TrollWalkConstants.HashLastUpdate, 0L);
            double elapsed = tick > 0 ? Math.Max(0.0, (ZNet.instance.GetTime() - new DateTime(tick)).TotalSeconds) : 0;
            float dist = Utils.DistanceXZ(target, lastPos);
            if (dist < 0.01f) return lastPos;

            float speed = Mathf.Max(TrollWalkConstants.MinSpeed,
                zdo.GetFloat(TrollWalkConstants.HashSpeed, TrollWalkConstants.DefaultSpeed));
            if (dist > TrollWalkConstants.RunDistance)
                speed = Mathf.Max(speed, TrollWalkConstants.VirtualRunSpeed);

            float travel = Mathf.Min(speed * (float)elapsed, dist);
            Vector3 dir = new Vector3((target.x - lastPos.x) / dist, 0f, (target.z - lastPos.z) / dist);
            return lastPos + dir * travel;
        }

        private void RemoveRoutePins(Minimap mm, RouteInfo ri)
        {
            if (mm == null) { ri.RoutePin = null; ri.TrackerPin = null; return; }
            if (ri.RoutePin != null) RemovePinInternal(mm, ri.RoutePin);
            if (ri.TrackerPin != null) RemovePinInternal(mm, ri.TrackerPin);
            ri.RoutePin = null;
            ri.TrackerPin = null;
            SetPinUpdateRequired(mm, true);
        }

        // ============ Безопасные вызовы пинов (без референса на Splatform) ============

        internal static Minimap.PinData AddPinSafe(Minimap mm, Vector3 pos, string name, Sprite icon)
        {
            if (s_addPin == null || mm == null) return null;
            try
            {
                if (s_puidDefault == null)
                {
                    ParameterInfo last = s_addPin.GetParameters().Last();
                    s_puidDefault = last.ParameterType.IsValueType
                        ? Activator.CreateInstance(last.ParameterType) : null;
                }
                Minimap.PinData pin = s_addPin.Invoke(mm,
                    new object[] { pos, Minimap.PinType.None, name, false, false, 0L, s_puidDefault }) as Minimap.PinData;
                if (pin != null)
                {
                    pin.m_icon = icon; // паттерн локационных пинов ванили
                    if (!string.IsNullOrEmpty(name))
                    {
                        try { pin.m_NamePinData = new Minimap.PinNameData(pin); } catch { }
                    }
                }
                return pin;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TrollWalk] AddPin failed: {e.Message}");
                return null;
            }
        }

        internal static void RemovePinInternal(Minimap mm, Minimap.PinData pin)
        {
            if (mm == null || pin == null || s_removePin == null) return;
            s_internalRemove = true;
            try { s_removePin.Invoke(mm, new object[] { pin }); }
            finally { s_internalRemove = false; }
        }

        internal static void SetPinUpdateRequired(Minimap mm, bool value)
        {
            try { if (mm != null && s_pinUpdateField != null) s_pinUpdateField(mm) = value; } catch { }
        }

        internal static bool IsOurPin(Minimap.PinData pin)
        {
            return pin != null &&
                   (pin.m_icon == TrollWalkIcons.Road || pin.m_icon == TrollWalkIcons.Troll);
        }

        internal static Minimap.PinData FindClosestOurPin(Minimap mm, Vector3 pos, float radius)
        {
            if (mm == null || s_pinsField == null) return null;
            List<Minimap.PinData> pins = s_pinsField(mm);
            if (pins == null) return null;
            Minimap.PinData best = null;
            float bestDist = radius;
            foreach (Minimap.PinData pin in pins)
            {
                if (!IsOurPin(pin)) continue;
                float d = Utils.DistanceXZ(pin.m_pos, pos);
                if (d <= bestDist) { bestDist = d; best = pin; }
            }
            return best;
        }

        internal static void HintBlocked()
        {
            if (Time.time - s_lastHint < 3f) return;
            s_lastHint = Time.time;
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                TrollWalkLoc.T("Troll routes are managed via [Y] on the troll", "Путями тролля управляют через [Y] у тролля"), 0, null, false);
        }

        internal static bool HandleOurPinRemoval(Minimap mm, Minimap.PinData pin)
        {
            if (pin == TrollWalkRouteSession.StagingPin)
            {
                if (TrollWalkRouteSession.Active) TrollWalkRouteSession.RemoveStaging(mm);
                return false;
            }
            if (TrollWalkRouteSession.Active)
            {
                ZDOID troll = Instance != null ? Instance.FindTrollByPin(pin) : ZDOID.None;
                if (troll != ZDOID.None && Instance != null) Instance.CancelRoute(troll, true);
                return false;
            }
            HintBlocked();
            return false;
        }
    }

    #endregion

    #region Сессия маршрута (Y → карта → пин пути → закрытие карты)

    public static class TrollWalkRouteSession
    {
        public static bool Active;
        public static bool IconSelected;
        public static ZDOID Troll = ZDOID.None;
        public static Minimap.PinData StagingPin;
        public static bool PinsVisible = true; // ПКМ по кнопке пина пути

        private static GameObject s_button;
        private static Image s_buttonIcon;
        private static readonly Color SelectedTint = new Color(1f, 0.85f, 0.3f);

        private static readonly AccessTools.FieldRef<Minimap, Image> s_icon0 =
            AccessTools.FieldRefAccess<Minimap, Image>("m_selectedIcon0");
        private static readonly AccessTools.FieldRef<Minimap, Image> s_icon1 =
            AccessTools.FieldRefAccess<Minimap, Image>("m_selectedIcon1");
        // тогл «Виден другим игрокам» — якорь для нашей кнопки
        private static readonly AccessTools.FieldRef<Minimap, Toggle> s_publicPos =
            AccessTools.FieldRefAccess<Minimap, Toggle>("m_publicPosition");

        internal static bool s_selectGuard;
        private static readonly MethodInfo s_selectIconMethod =
            AccessTools.Method(typeof(Minimap), "SelectIcon");

        // ============ Начало сессии: карта открывается СРАЗУ ============

        public static void Begin(ZDOID troll)
        {
            Active = true;
            IconSelected = false;
            Troll = troll;
            StagingPin = null;

            TrollWalkIcons.EnsureLoaded();

            // СНАЧАЛА карта (UI активен и разложен, все rect валидны),
            // ПОТОМ строим кнопку
            Minimap mm = Minimap.instance;
            if (mm != null) mm.SetMapMode(Minimap.MapMode.Large);

            BuildToolbarButton();
            SetIconSelected(true); // авто-выбор пина пути (+ SelectIcon(Icon0) под ваниль)

            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                TrollWalkLoc.T("Click the map to place the route pin, then close the map to send the troll",
                    "Кликните по карте, чтобы поставить пин пути, затем закройте карту — тролль отправится"), 0, null, false);
            Debug.Log($"[TrollWalk] Route session begin: troll={troll}");
        }

        public static void SetIconSelected(bool on)
        {
            IconSelected = on;
            if (s_buttonIcon != null)
                s_buttonIcon.color = on ? SelectedTint : Color.white;

            if (on)
            {
                // гарантируем «размещаемый» ванильный тип, иначе клик по карте
                // не дойдёт до ShowPinNameInput
                Minimap mm = Minimap.instance;
                if (mm != null && s_selectIconMethod != null)
                {
                    s_selectGuard = true;
                    try { s_selectIconMethod.Invoke(mm, new object[] { Minimap.PinType.Icon0 }); }
                    finally { s_selectGuard = false; }
                }
            }
        }

        public static void TogglePinsVisible()
        {
            PinsVisible = !PinsVisible;
            TrollWalkManager.Instance?.UpdatePins();
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                PinsVisible
                    ? TrollWalkLoc.T("Troll route pins: shown", "Пины маршрутов троллей: показаны")
                    : TrollWalkLoc.T("Troll route pins: hidden", "Пины маршрутов троллей: скрыты"), 0, null, false);
        }

        public static void RemoveStaging(Minimap mm)
        {
            if (StagingPin == null) return;
            TrollWalkManager.RemovePinInternal(mm, StagingPin);
            StagingPin = null;
            TrollWalkManager.SetPinUpdateRequired(mm, true);
        }

        // Выход с карты = применение (ОБЯЗАТЕЛЬНЫЙ выход)
        public static void CommitAndClose()
        {
            Minimap mm = Minimap.instance;
            try
            {
                if (mm != null && StagingPin != null && Troll != ZDOID.None && TrollWalkManager.Instance != null)
                {
                    string name = StagingPin.m_name;
                    if (string.IsNullOrEmpty(name))
                        name = TrollWalkLoc.T("Route", "Путь");
                    TrollWalkManager.Instance.Dispatch(Troll, StagingPin.m_pos, name);
                }
            }
            finally
            {
                if (mm != null) RemoveStaging(mm);
                Active = false;
                IconSelected = false;
                Troll = ZDOID.None;
                DestroyToolbarButton();
            }
        }

        public static void HardReset()
        {
            Minimap mm = Minimap.instance;
            if (mm != null) RemoveStaging(mm);
            Active = false;
            IconSelected = false;
            Troll = ZDOID.None;
            DestroyToolbarButton();
        }

        // ============ Кнопка «пин пути»: с нуля, ребёнок тогла
        // «Виден другим игрокам», НАД ним. Размер — как у ванильных иконок. ============

        private static void BuildToolbarButton()
        {
            try
            {
                Minimap mm = Minimap.instance;
                if (mm == null) return;

                // эталон размера — ванильная иконка пина (m_selectedIcon0)
                Image refIcon = s_icon0 != null ? s_icon0(mm) : null;
                RectTransform refRt = refIcon != null ? (RectTransform)refIcon.transform : null;

                Vector2 iconWorldSize = Vector2.zero; // размер эталона в мировых координатах
                if (refRt != null && refRt.parent != null)
                {
                    // layout мог не отработать в этом кадре — форсируем
                    LayoutRebuilder.ForceRebuildLayoutImmediate(refRt.parent as RectTransform);
                    if (refRt.rect.width > 1f && refRt.rect.height > 1f)
                    {
                        Vector3 ls = refRt.lossyScale;
                        iconWorldSize = new Vector2(
                            refRt.rect.width * Mathf.Abs(ls.x),
                            refRt.rect.height * Mathf.Abs(ls.y));
                    }
                }

                GameObject go = new GameObject("TrollWalkRouteIcon");
                Image img = go.AddComponent<Image>();
                img.sprite = TrollWalkIcons.Road;
                img.raycastTarget = true;
                img.preserveAspect = true;

                RectTransform rt = (RectTransform)go.transform;

                Toggle anchor = null;
                try { anchor = s_publicPos != null ? s_publicPos(mm) : null; } catch { }

                if (anchor != null)
                {
                    RectTransform toggleRt = (RectTransform)anchor.transform;
                    go.transform.SetParent(anchor.transform, false);

                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.localRotation = Quaternion.identity;
                    rt.localScale = Vector3.one;

                    // мировой размер эталона -> локальный размер под родителем тогла
                    Vector2 ts = toggleRt.lossyScale;
                    Vector2 size = new Vector2(
                        iconWorldSize.x / Mathf.Max(0.0001f, Mathf.Abs(ts.x)),
                        iconWorldSize.y / Mathf.Max(0.0001f, Mathf.Abs(ts.y)));
                    if (size.x < 8f || size.y < 8f) size = new Vector2(44f, 44f);

                    rt.sizeDelta = size;

                    float toggleH = Mathf.Max(toggleRt.rect.height, 24f);
                    // низ кнопки на 6px выше верха тогла
                    rt.anchoredPosition = new Vector2(0f, toggleH * 0.5f + size.y * 0.5f + 6f);
                }
                else
                {
                    // фолбэк: под колонкой иконок пинов
                    if (refRt == null)
                    {
                        UnityEngine.Object.Destroy(go);
                        Debug.LogWarning("[TrollWalk] Route button: no anchor and no icon row, aborted");
                        return;
                    }
                    go.transform.SetParent(refRt.parent, false);

                    rt.anchorMin = refRt.anchorMin;
                    rt.anchorMax = refRt.anchorMax;
                    rt.pivot = refRt.pivot;
                    rt.localRotation = Quaternion.identity;
                    rt.localScale = Vector3.one;

                    Vector2 size = iconWorldSize;
                    if (refRt.lossyScale.sqrMagnitude > 0f)
                        size /= Mathf.Max(0.0001f, Mathf.Abs(refRt.lossyScale.x));
                    if (size.x < 8f || size.y < 8f) size = new Vector2(44f, 44f);
                    rt.sizeDelta = size;

                    Image icon1 = s_icon1 != null ? s_icon1(mm) : null;
                    float spacingY = size.y + 6f;
                    if (icon1 != null)
                    {
                        float dy = Mathf.Abs(((RectTransform)icon1.transform).anchoredPosition.y - refRt.anchoredPosition.y);
                        if (dy > 1f) spacingY = dy;
                    }
                    rt.anchoredPosition = refRt.anchoredPosition - new Vector2(0f, spacingY);

                    LayoutElement le = go.AddComponent<LayoutElement>();
                    le.ignoreLayout = true;
                    le.minWidth = size.x;
                    le.minHeight = size.y;
                }

                rt.SetAsLastSibling();

                // интерактивность: ЛКМ — выбор, ПКМ — видимость пинов маршрутов
                UIInputHandler handler = go.AddComponent<UIInputHandler>();
                handler.m_onLeftDown += OnToolbarDown;
                handler.m_onRightClick += OnToolbarRight;

                s_button = go;
                s_buttonIcon = img;

                Debug.Log($"[TrollWalk] Route button built: refWorldSize={iconWorldSize}, " +
                           $"size={rt.sizeDelta}, parent='{(rt.parent ? rt.parent.name : "none")}', " +
                           $"localPos={rt.anchoredPosition}, activeInHierarchy={go.activeInHierarchy}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TrollWalk] Toolbar button failed: {e}");
            }
        }

        private static void OnToolbarDown(UIInputHandler h)
        {
            SetIconSelected(!IconSelected);
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                IconSelected
                    ? TrollWalkLoc.T("Route pin selected — click the map", "Пин пути выбран — кликните по карте")
                    : TrollWalkLoc.T("Route pin deselected", "Пин пути снят с выбора"), 0, null, false);
        }

        private static void OnToolbarRight(UIInputHandler h)
        {
            TogglePinsVisible();
        }

        private static void DestroyToolbarButton()
        {
            if (s_button != null) UnityEngine.Object.Destroy(s_button);
            s_button = null;
            s_buttonIcon = null;
        }
    }

    #endregion

    #region Harmony-патчи

    [HarmonyPatch]
    public static class TrollWalkPatches
    {
        private static bool IsTroll(Character c) =>
            c != null && c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);

        private static readonly AccessTools.FieldRef<Minimap, Minimap.PinData> s_namePinRef =
            AccessTools.FieldRefAccess<Minimap, Minimap.PinData>("m_namePin");
        private static readonly AccessTools.FieldRef<Minimap, bool> s_wasFocusedRef =
            AccessTools.FieldRefAccess<Minimap, bool>("m_wasFocused");
        private static readonly AccessTools.FieldRef<Minimap, GuiInputField> s_nameInputRef =
            AccessTools.FieldRefAccess<Minimap, GuiInputField>("m_nameInput");

        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        private static void Character_Awake_Postfix(Character __instance)
        {
            if (__instance == null || !IsTroll(__instance)) return;
            if (__instance.GetComponent<TrollWalkController>() == null)
                __instance.gameObject.AddComponent<TrollWalkController>();
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        [HarmonyPostfix]
        private static void MonsterAI_UpdateAI_Postfix(MonsterAI __instance, float dt)
        {
            if (__instance == null) return;
            TrollWalkController ctrl = __instance.GetComponent<TrollWalkController>();
            if (ctrl != null) ctrl.TickAI(dt);
        }

        // Во время маршрута ванильный idle отключается (анти-кручение)
        [HarmonyPatch(typeof(BaseAI), "IdleMovement")]
        [HarmonyPrefix]
        private static bool BaseAI_IdleMovement_Prefix(BaseAI __instance)
        {
            if (__instance == null) return true;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !IsTroll(c)) return true;

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return true;
            return !nv.GetZDO().GetBool(TrollWalkConstants.HashActive, false);
        }

        // В пути тролль НЕ ищет цели сам (олени/зайцы не останавливают маршрут)
        [HarmonyPatch(typeof(BaseAI), "FindEnemy")]
        [HarmonyPrefix]
        private static bool BaseAI_FindEnemy_Prefix(BaseAI __instance, ref Character __result)
        {
            if (__instance == null) return true;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !IsTroll(c)) return true;

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return true;
            if (!nv.GetZDO().GetBool(TrollWalkConstants.HashActive, false)) return true;

            __result = null;
            return false;
        }

        // ============ Ховер: через Tameable ============

        [HarmonyPatch(typeof(Tameable), "GetHoverText")]
        [HarmonyPostfix]
        private static void Tameable_GetHoverText_Postfix(Tameable __instance, ref string __result)
        {
            if (__instance == null) return;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !IsTroll(c) || !c.IsTamed() || c.IsDead()) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return;
            ZDO zdo = nv.GetZDO();

            if (zdo.GetBool(TrollWalkConstants.HashActive, false))
            {
                string rn = zdo.GetString(TrollWalkConstants.HashName, "");
                __result += $"\n<color=#aaccff>{TrollWalkLoc.T("On the way:", "В пути:")} {rn}</color>";
            }
            __result += $"\n[<color=yellow><b>Y</b></color>] {TrollWalkLoc.T("Send to waypoint (map)", "Отправить (карта)")}";
        }

        // Тролль «принимает имя пути» до конца пути
        [HarmonyPatch(typeof(Tameable), "GetHoverName")]
        [HarmonyPostfix]
        private static void Tameable_GetHoverName_Postfix(Tameable __instance, ref string __result)
        {
            if (__instance == null) return;
            Character c = __instance.GetComponent<Character>();
            if (c == null || !IsTroll(c) || !c.IsTamed()) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return;
            ZDO zdo = nv.GetZDO();
            if (!zdo.GetBool(TrollWalkConstants.HashActive, false)) return;
            string rn = zdo.GetString(TrollWalkConstants.HashName, "");
            if (!string.IsNullOrEmpty(rn)) __result = rn;
        }

        // ============ [Y]: карта сразу ============

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Player_Update_Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || __instance.IsDead()) return;
            if (!Input.GetKeyDown(KeyCode.Y)) return;
            if (TextInput.IsVisible() || Minimap.InTextInput()) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;
            if (global::Console.IsVisible() || Menu.IsActive() || InventoryGui.IsVisible() || Hud.InRadial()) return;
            if (TrollWalkRouteSession.Active) return;

            GameObject hover = __instance.GetHoverObject();
            if (hover == null) return;
            if (hover.GetComponentInParent<TrollPieceTag>() != null) return;
            Character troll = hover.GetComponentInParent<Character>();
            if (troll == null || !IsTroll(troll) || !troll.IsTamed() || troll.IsDead()) return;
            ZNetView nv = troll.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return;

            TrollWalkRouteSession.Begin(nv.GetZDO().m_uid);
        }

        // ============ Менеджер ============

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void ZNetScene_Awake_Postfix()
        {
            TrollWalkManager.EnsureCreated();
        }

        [HarmonyPatch(typeof(Minimap), "UpdateDynamicPins")]
        [HarmonyPostfix]
        private static void Minimap_UpdateDynamicPins_Postfix()
        {
            TrollWalkManager.Instance?.UpdatePins();
        }

        // Выход с карты = применение маршрута (ОБЯЗАТЕЛЬНЫЙ выход)
        [HarmonyPatch(typeof(Minimap), "SetMapMode")]
        [HarmonyPostfix]
        private static void Minimap_SetMapMode_Postfix(Minimap __instance, Minimap.MapMode mode)
        {
            if (!TrollWalkRouteSession.Active) return;
            if (mode == Minimap.MapMode.Large) return;
            TrollWalkRouteSession.CommitAndClose();
        }

        // Выбор ванильной иконки снимает выбор нашей
        [HarmonyPatch(typeof(Minimap), "SelectIcon")]
        [HarmonyPostfix]
        private static void Minimap_SelectIcon_Postfix()
        {
            if (TrollWalkRouteSession.s_selectGuard || !TrollWalkRouteSession.Active) return;
            if (TrollWalkRouteSession.IconSelected) TrollWalkRouteSession.SetIconSelected(false);
        }

        // ============ Постановка пина пути: ванильный ввод имени ============

        [HarmonyPatch(typeof(Minimap), "ShowPinNameInput")]
        [HarmonyPrefix]
        private static bool Minimap_ShowPinNameInput_Prefix(Minimap __instance, Vector3 pos)
        {
            if (!TrollWalkRouteSession.Active || !TrollWalkRouteSession.IconSelected) return true;

            Minimap.PinData pin = TrollWalkRouteSession.StagingPin;
            if (pin == null)
            {
                pin = TrollWalkManager.AddPinSafe(__instance, pos, "", TrollWalkIcons.Road);
                if (pin != null) TrollWalkRouteSession.StagingPin = pin;
            }
            else
            {
                pin.m_pos = pos; // повторный клик — переносим точку
                TrollWalkManager.SetPinUpdateRequired(__instance, true);
            }
            if (pin == null) return true; // сбой — пусть ваниль

            GuiInputField input = s_nameInputRef(__instance);
            if (input == null) return true;

            s_namePinRef(__instance) = pin; // ваниль запишет имя в наш пин
            input.text = "";
            input.gameObject.SetActive(true);

            if (ZInput.IsExclusiveGamepadActive() && !ZInput.IsTouchActive())
            {
                input.gameObject.transform.localPosition = new Vector3(0f, -30f, 0f);
            }
            else
            {
                RectTransform parentRect = input.gameObject.transform.parent != null
                    ? input.gameObject.transform.parent.GetComponent<RectTransform>() : null;
                if (parentRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        parentRect, ZInput.pointerPosition, null, out Vector2 v))
                    input.gameObject.transform.localPosition = new Vector3(v.x, v.y - 30f);
            }

            input.ActivateInputField();
            s_wasFocusedRef(__instance) = true;
            return false;
        }

        // ============ Защита пинов ============

        [HarmonyPatch(typeof(Minimap), "RemovePin", new[] { typeof(Minimap.PinData) })]
        [HarmonyPrefix]
        private static bool Minimap_RemovePin_Prefix(Minimap __instance, Minimap.PinData pin)
        {
            if (TrollWalkManager.s_internalRemove) return true;
            if (!TrollWalkManager.IsOurPin(pin)) return true;
            return TrollWalkManager.HandleOurPinRemoval(__instance, pin);
        }

        [HarmonyPatch(typeof(Minimap), "RemovePin", new[] { typeof(Vector3), typeof(float) })]
        [HarmonyPrefix]
        private static bool Minimap_RemovePin_Pos_Prefix(Minimap __instance, Vector3 pos, float radius)
        {
            if (TrollWalkManager.s_internalRemove) return true;
            Minimap.PinData our = TrollWalkManager.FindClosestOurPin(__instance, pos, radius);
            if (our == null) return true;
            return TrollWalkManager.HandleOurPinRemoval(__instance, our);
        }

        // ============ Синхронизация ZDO активных троллей всем клиентам ============

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyPostfix]
        private static void ZDOMan_CreateSyncList_Postfix(ZDOMan __instance, List<ZDO> toSync)
        {
            try
            {
                if (toSync == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (TrollWalkManager.Instance == null) return;
                List<ZDOID> ids = TrollWalkManager.Instance.ActiveRouteIds;
                if (ids.Count == 0) return;
                foreach (ZDOID id in ids)
                {
                    ZDO zdo = __instance.GetZDO(id);
                    if (zdo != null && zdo.Persistent && !toSync.Contains(zdo)) toSync.Add(zdo);
                }
            }
            catch { }
        }
    }

    #endregion
}