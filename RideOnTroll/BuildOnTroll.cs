using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

// ============================================================================
//  ИСТОРИЯ ФИКСОВ:
//  FIX 1..6: сохранность построек.
//  FIX 9..16: итерации платформенной навигации (сетка/A*/ломание).
//  FIX 13: восстановление потерянной платформы.
//  FIX 17 (текущее, ГИБРИДНАЯ АРХИТЕКТУРА):
//   17a. РЕЖИМЫ: навигатор и ломание работают ТОЛЬКО в режиме маршрута
//        (TrollWalk, ZDO-флаг HashActive) и только при наличии построек.
//        Следование/охрана/idle/дикие тролли/без платформы — чистая ваниль.
//   17b. ВАНИЛЬ ВЕДЁТ: путь берётся из BaseAI.FindPath (рельеф, склоны,
//        вода). Мод НЕ строит свой путь — 2D-сетку 40x40 и A* удалены.
//   17c. МОД КОНТРОЛИРУЕТ ГАБАРИТ: вдоль ванильного пути выполняется
//        OverlapBox объёмом платформы (клиренс 1.4 м от земли — низкие
//        валуны/жилы перешагиваются). Чисто — идём по ванильному пути.
//   17d. КАСАТЕЛЬНЫЙ ОБЪЕЗД: коробка заблокирована — пробуем отклонения
//        ±25/45/65° (гистерезис стороны). Ломание — ТОЛЬКО когда объезд
//        невозможен (это и решает «ломает лишнее»).
//   17e. СООБЩЕНИЕ «не протиснуться» — только по физическому простою
//        ≥4.5 с в маршрутном режиме. Никаких сообщений от планировщика.
//   17f. Ломание: только вперёд (Dot>=0.3), без MineRock, без данжей,
//        подход в упор (3 м), урон 250 / toolTier 4.
// ============================================================================
namespace TrollBuildingMod
{
    public enum TrollBuildTargetType { None, TrollSkin, TrollPiece }

    public static class TrollBuildConstants
    {
        public const string KeyTrollUUID = "TrollBuild_TrollUUID";
        public const string KeyParentPieceUID = "TrollBuild_ParentPieceUID";
        public const string KeyLocalPos = "TrollBuild_LocalPos";
        public const string KeyLocalRotQ = "TrollBuild_LocalRotQ";
        public const string KeyHasLocal = "TrollBuild_HasLocal";

        public static readonly int HashTrollUUID = KeyTrollUUID.GetStableHashCode();
        public static readonly int HashLocalPos = KeyLocalPos.GetStableHashCode();
        public static readonly int HashLocalRotQ = KeyLocalRotQ.GetStableHashCode();
        public static readonly int HashHasLocal = KeyHasLocal.GetStableHashCode();
        public static readonly KeyValuePair<int, int> ParentPieceHashPair = ZDO.GetHashZDOID(KeyParentPieceUID);

        public static readonly Vector3 DefaultBuildRootOffset = new Vector3(0f, 3.35f, -0.25f);
        public static readonly Vector3 BuildZoneHalfExtents = new Vector3(1.75f, 1.25f, 1.75f);
        public const float ZoneAcceptanceMult = 2f;
        public const float TrollExtraReach = 4f;
        public const float MaxTurnSpeedWithPieces = 60f;
        public const float RotationHoldMaxTime = 2f;

        public static bool IsDedicatedServer => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    public static class TrollBuildContext
    {
        public static TrollBuildTargetType TargetType = TrollBuildTargetType.None;
        public static TrollPiecesContainer Container;
        public static Character TrollCharacter;
        public static Piece TargetPiece;
        public static Vector3 AimPoint = Vector3.zero;
        public static Vector3 AimNormal = Vector3.up;

        public static bool IsActive => TargetType != TrollBuildTargetType.None && Container != null;
        public static Transform BuildRoot => Container != null ? Container.BuildRoot : null;

        public static void SetTargetSkin(TrollPiecesContainer c, Vector3 aim, Vector3 normal)
        {
            TargetType = TrollBuildTargetType.TrollSkin;
            Container = c; TrollCharacter = c != null ? c.TrollCharacter : null; TargetPiece = null;
            AimPoint = aim; AimNormal = normal;
        }

        public static void SetTargetPiece(TrollPiecesContainer c, Piece p, Vector3 aim, Vector3 normal)
        {
            TargetType = TrollBuildTargetType.TrollPiece;
            Container = c; TrollCharacter = c != null ? c.TrollCharacter : null; TargetPiece = p;
            AimPoint = aim; AimNormal = normal;
        }

        public static void Reset()
        {
            TargetType = TrollBuildTargetType.None;
            Container = null; TrollCharacter = null; TargetPiece = null;
            AimPoint = Vector3.zero; AimNormal = Vector3.up;
        }
    }

    public class TrollPieceTag : MonoBehaviour
    {
        public TrollPiecesContainer Container;
        public float AttachedAt; // FIX 2
    }

    public static class TrollMaterialFixer
    {
        private static readonly int TriplanarLocalPos = Shader.PropertyToID("_TriplanarLocalPos");
        private static readonly int RippleDistance = Shader.PropertyToID("_RippleDistance");
        private static readonly int ValueNoise = Shader.PropertyToID("_ValueNoise");
        private static readonly Dictionary<Material, Material> s_cache = new Dictionary<Material, Material>();

        public static void FixPieceMaterials(ZNetView netView)
        {
            if (TrollBuildConstants.IsDedicatedServer || netView == null) return;

            foreach (Renderer r in netView.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null) continue;
                if (r.GetComponentInParent<Switch>() != null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;

                Material[] mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = FixMaterial(mats[i]);
                    if (m != null && m != mats[i]) { mats[i] = m; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static Material FixMaterial(Material original)
        {
            if (original == null) return null;
            if (!original.HasProperty(RippleDistance) && !original.HasProperty(ValueNoise) && !original.HasProperty(TriplanarLocalPos))
                return original;

            string shader = original.shader != null ? original.shader.name : "";
            if (shader.Contains("Vegetation") || shader.Contains("Rock") || shader.Contains("Terrain"))
                return original;

            if (s_cache.TryGetValue(original, out Material cached)) return cached;

            Material m = new Material(original);
            if (m.HasProperty(ValueNoise)) m.SetFloat(ValueNoise, 0f);
            if (m.HasProperty(RippleDistance)) m.SetFloat(RippleDistance, 0f);
            if (m.HasProperty(TriplanarLocalPos)) m.SetFloat(TriplanarLocalPos, 1f);
            s_cache[original] = m;
            return m;
        }
    }

    // ========================================================================
    //  ЛУЧЕВОЙ НАВИГАТОР (FIX 18)
    //  Алгоритм: толстый луч (BoxCast шириной платформы) ПРЯМО к цели;
    //  препятствие -> веер альтернативных лучей; свободных мест нет ->
    //  ломание блокатора. Полный рантайм: пересчёт каждые 0.1 с, никаких
    //  сеток/A*/запечённых путей — объекты, прогрузившиеся по ходу,
    //  учитываются автоматически.
    //  Порядок решений: прямой луч -> малый веер (до 45°) -> ЛОМАНИЕ
    //  блокатора -> широкий веер (до 150°) -> наименее перекрытое
    //  направление (скольжение вдоль стены) -> стоп + сообщение.
    //  Работает только в режиме маршрута TrollWalk при наличии построек.
    // ========================================================================
    public class TrollPlatformNavigator
    {
        // ---------- конфигурация ----------
        private const float MainRayLength = 5.0f;     // главный луч вперёд
        private const float SideRayLength = 3.5f;     // длина боковых лучей
        private const float ProbeHalfThick = 0.5f;    // толщина луча вдоль движения
        private const float SideMargin = 0.25f;      // запас по ширине
        private const float StepClearance = 1.4f;     // ниже — перешагиваем
        private const float CastInterval = 0.1f;      // рантайм-пересчёт (10 Гц)

        // малый веер: касательный обход
        private static readonly float[] SteerAngles = { 10f, 22f, 35f, 45f };
        // широкий веер: скольжение вокруг не-ломаемых стен
        private static readonly float[] WideAngles = { 60f, 80f, 100f, 125f, 150f };

        // ломание
        private const float BreakPickMax = 7f;         // блокатор дальше — не выбираем
        private const float BreakSwingRange = 3.0f;   // дистанция удара
        private const float BreakSwingInterval = 1.15f;
        private const float BreakDamage = 250f;
        private const int BreakToolTier = 4;
        private const float BreakTimeout = 25f;       // от последнего удара
        private const float ForwardDotMin = 0.25f;    // цель только впереди
        private const float FailedTreeMemory = 30f;
        private const float FailedTreeRadius = 3f;

        // простой/сообщения
        private const float StallMsgTime = 6f;         // секунд стояния до сообщения
        private const float StallSideFlip = 3f;        // смена стороны объезда при простое
        private const float DiagLogInterval = 3f;

        // габарит
        private const float DetailProbeRadius = 0.75f;
        private const float FootMaxHalfExtent = 12f;
        private const float FootMaxCenterOffset = 8f;
        private const float FootMaxRadius = 16f;
        private const float FootGarbageDist = 25f;

        private static readonly int ObstacleMask =
            LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");
        private static readonly RaycastHit[] s_castHits = new RaycastHit[16];
        private static readonly Collider[] s_hits = new Collider[32];

        private static readonly Type s_treeSyncType = FindTreeTypeByName("TreeSync");
        private static readonly Type s_treeLogType = FindTreeTypeByName("TreeLog");

        private static Type FindTreeTypeByName(string name)
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

        // коллайдеры архитектуры данжей — не цели ломания
        private static bool IsDungeonCollider(Collider c)
        {
            string n = c.gameObject.name;
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.Contains("base_model") || n.Contains("entrance") ||
                   n.Contains("dungeon") || n.Contains("crypt") || n.Contains("cube");
        }

        private class BreakTarget
        {
            public MonoBehaviour Comp;
            public IDestructible Destructible;
            public Vector3 Pos;
        }

        private readonly TrollPiecesContainer m_owner;

        // рантайм-состояние (пересчитывается каждые CastInterval)
        private float m_lastCast = -1f;
        private bool m_moveDirValid;
        private Vector3 m_moveDir;
        private Collider m_forwardBlocker;      // ближайшее чужое, что перекрыло главный луч
        private bool m_wideFreeValid;
        private Vector3 m_wideFreeDir;
        private bool m_leastDirValid;
        private Vector3 m_leastDir;
        private float m_leastFree;

        private int m_steerSide;                // гистерезис стороны (—1/0/+1)
        private float m_stallTimer;
        private Vector3 m_lastStallPos;
        private float m_lastFlipMark;
        private float m_lastBlockedMsg = -30f;
        private float m_lastTreeMsg = -30f;
        private float m_lastDiagLog = -30f;

        // ломание
        private BreakTarget m_break;
        private float m_lastLandedSwing = -30f;
        private float m_nextSwing;
        private int m_swingCount;
        private readonly List<Vector3> m_failedTreePos = new List<Vector3>();

        // выпеченный габарит
        private int m_footVersion = -1;
        private readonly List<Vector2> m_footPoints = new List<Vector2>();
        private float[] m_footHeights = { 3f };
        private float m_footRadius = 0f;
        private float m_boxHalfX = 1.5f;
        private float m_spanMin = 2f;
        private float m_spanMax = 4.5f;
        private float m_spanMid = 3.2f;
        private float m_spanHalf = 1.2f;

        public TrollPlatformNavigator(TrollPiecesContainer owner)
        {
            m_owner = owner;
        }

        public void Invalidate()
        {
            m_footVersion = -1;
        }

        public bool IsBreaking => m_break != null;

        public void OnRouteEnded()
        {
            m_break = null;
            m_steerSide = 0;
            m_stallTimer = 0f;
            m_moveDirValid = false;
            m_wideFreeValid = false;
            m_leastDirValid = false;
        }

        public float FootprintRadius
        {
            get { BakeFootprintIfNeeded(); return m_footRadius; }
        }

        // ====================================================================
        //  Основной вход (патч BaseAI.MoveTo; вызывается только в режиме
        //  маршрута TrollWalk при наличии построек — гейт в хуке).
        // ====================================================================
        public bool DriveMoveTo(BaseAI ai, float dt, Vector3 point, float dist, bool run)
        {
            Vector3 pos = ai.transform.position;
            float arrive = Mathf.Max(dist, run ? 1f : 0.5f);
            if (Utils.DistanceXZ(point, pos) <= arrive)
            {
                ai.StopMoving();
                m_break = null;
                m_stallTimer = 0f;
                return true;
            }

            if (m_break != null)
                return UpdateBreaking(ai, dt);

            Vector3 to = point - pos;
            to.y = 0f;
            float d = to.magnitude;
            if (d < 0.05f)
            {
                ai.StopMoving();
                TrackStall(pos, dt);
                return false;
            }
            Vector3 dir = to / d;   // луч ПРЯМО к цели

            // рантайм-пересчёт лучей (прогрузка объектов учитывается сама)
            if (Time.time - m_lastCast >= CastInterval)
            {
                m_lastCast = Time.time;
                Recast(pos, dir);
            }

            // 1) прямой либо касательный коридор найден — идём
            if (m_moveDirValid)
            {
                ai.MoveTowards(m_moveDir, run);
                TrackStall(pos, dt);
                return false;
            }

            // 2) всё перекрыто на малых углах — ломаем блокатор главного луча
            if (m_forwardBlocker != null && TryStartBreaking(ai, pos, dir))
                return false;

            // 3) блокатор не ломается — широкий свободный коридор
            if (m_wideFreeValid)
            {
                ai.MoveTowards(m_wideFreeDir, run);
                TrackStall(pos, dt);
                return false;
            }

            // 4) не ломается и всё перекрыто — скользим вдоль стены
            //    (наименее перекрытое направление — НИКОГДА не стоим на месте)
            if (m_leastDirValid)
            {
                ai.MoveTowards(m_leastDir, run);
                TrackStall(pos, dt);
                return false;
            }

            ai.StopMoving();
            TrackStall(pos, dt);
            return false;
        }

        // ====================================================================
        //  РУНТАЙМ-ПЕРЕСЧЁТ ЛУЧЕЙ
        // ====================================================================
        private void Recast(Vector3 pos, Vector3 dir)
        {
            m_moveDirValid = false;
            m_wideFreeValid = false;
            m_leastDirValid = false;
            m_forwardBlocker = null;
            m_leastFree = -1f;

            // главный луч прямо к цели
            float fwd = CastDir(pos, dir, MainRayLength, out Collider fwdBlocker);
            m_forwardBlocker = fwdBlocker;
            m_leastDir = dir;
            m_leastFree = fwd;

            if (fwdBlocker == null)
            {
                m_moveDir = dir;
                m_moveDirValid = true;
                return; // путь чист
            }

            int pref = m_steerSide >= 0 ? 1 : -1;

            // малый веер: касательный обход (предпочтительная сторона первой)
            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? pref : -pref;
                foreach (float a in SteerAngles)
                {
                    Vector3 cand = Quaternion.Euler(0f, a * sign, 0f) * dir;
                    if (CastDir(pos, cand, SideRayLength, out _) >= SideRayLength - 0.01f)
                    {
                        m_moveDir = cand;
                        m_moveDirValid = true;
                        m_steerSide = (int)sign;
                        return; // обход найден — ломание не нужно
                    }
                }
            }

            // малых углов нет: главный блокатор пойдёт на ломание (в DriveMoveTo),
            // здесь готовим широкий веер — запасной путь для не-ломаемых стен
            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? pref : -pref;
                foreach (float a in WideAngles)
                {
                    Vector3 cand = Quaternion.Euler(0f, a * sign, 0f) * dir;
                    float free = CastDir(pos, cand, SideRayLength, out _);

                    if (free >= SideRayLength - 0.01f)
                    {
                        m_wideFreeDir = cand;
                        m_wideFreeValid = true;
                        break; // первый свободный широкий коридор достаточен
                    }
                    if (free > m_leastFree + 0.5f)
                    {
                        m_leastFree = free;
                        m_leastDir = cand;
                        m_leastDirValid = true;
                    }
                }
                if (m_wideFreeValid) break;
            }
        }

        // Толстый луч = BoxCast шириной платформы, высотой её спана,
        // с клиренсом StepClearance (низкие валуны/жилы не видит — перешагиваем).
        // Возвращает свободную длину (== length, если чисто) и ближайший
        // ЧУЖОЙ блокатор.
        private float CastDir(Vector3 pos, Vector3 dir, float length, out Collider blocker)
        {
            blocker = null;
            BakeFootprintIfNeeded();
            float ground = GroundY(pos);
            Vector3 origin = new Vector3(pos.x, ground + m_spanMid, pos.z)
                + dir * (ProbeHalfThick + 0.1f);
            Vector3 half = new Vector3(m_boxHalfX + SideMargin, m_spanHalf + 0.1f, ProbeHalfThick);

            int n = Physics.BoxCastNonAlloc(origin, half, dir, s_castHits,
                Quaternion.LookRotation(dir), length, ObstacleMask, QueryTriggerInteraction.Ignore);

            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = s_castHits[i].collider;
                if (!IsForeign(c)) continue;
                if (s_castHits[i].distance < nearest)
                {
                    nearest = s_castHits[i].distance;
                    blocker = c;
                }
            }
            return blocker == null ? length : nearest;
        }

        // ====================================================================
        //  ЛОМАНИЕ — блокатор главного луча, когда обход малыми углами невозможен
        // ====================================================================
        private bool TryStartBreaking(BaseAI ai, Vector3 pos, Vector3 dir)
        {
            if (m_owner.TrollCharacter == null) return false;
            if (m_owner.TrollCharacter.IsDead()) return false;

            if (ai.GetTargetCreature() != null)
            {
                if (Time.time - m_lastDiagLog > DiagLogInterval)
                {
                    m_lastDiagLog = Time.time;
                    Debug.Log("[TrollBuild] Break suppressed: troll has target creature '" +
                              ai.GetTargetCreature().name + "'");
                }
                return false;
            }

            Collider c = m_forwardBlocker;
            if (IsDungeonCollider(c)) return false;

            MonoBehaviour mb = FindBreakableComponent(c);
            if (mb == null)
            {
                if (Time.time - m_lastDiagLog > DiagLogInterval)
                {
                    m_lastDiagLog = Time.time;
                    Debug.Log("[TrollBuild] Ray blocked by NON-breakable: " +
                              c.gameObject.name + "/" + LayerMask.LayerToName(c.gameObject.layer));
                }
                return false;
            }

            // постройки, платформы, существа — НИКОГДА
            if (mb.GetComponentInParent<Piece>() != null) return false;
            if (mb.GetComponentInParent<TrollPieceTag>() != null) return false;
            if (mb.GetComponentInParent<Character>() != null) return false;
            if (mb.transform.IsChildOf(m_owner.transform)) return false;
            if (IsRecentlyFailedTree(mb.transform.position)) return false;

            Vector3 cp = ClosestPointOn(c, pos);

            // цель — только впереди по ходу движения
            Vector3 to = cp - pos;
            to.y = 0f;
            if (to.sqrMagnitude < 0.01f) return false;
            if (Vector3.Dot(dir, to.normalized) < ForwardDotMin) return false;

            float d = Utils.DistanceXZ(cp, pos);
            if (d > BreakPickMax) return false;

            m_break = new BreakTarget
            {
                Comp = mb,
                Destructible = mb as IDestructible,
                Pos = cp
            };
            m_lastLandedSwing = Time.time;
            m_nextSwing = 0f;
            m_swingCount = 0;

            Debug.Log($"[TrollBuild] Break START: '{mb.gameObject.name}' " +
                      $"({mb.GetType().Name}) dist={d:F1} (no free rays)");

            if (Time.time - m_lastTreeMsg > 10f)
            {
                m_lastTreeMsg = Time.time;
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                    TrollWalkLoc.T("The troll is clearing the way",
                        "Тролль расчищает путь"), 0, null, false);
            }
            return true;
        }

        private static Vector3 ClosestPointOn(Collider c, Vector3 to)
        {
            try
            {
                MeshCollider mc = c as MeshCollider;
                if (mc == null || mc.convex)
                    return c.ClosestPoint(to);
            }
            catch { }
            return c.bounds.ClosestPoint(to);
        }

        // БЕЗ MineRock/MineRock5 — низкое перешагивается (клиренс),
        // высокое объезжается веером.
        private static MonoBehaviour FindBreakableComponent(Collider c)
        {
            Component comp = c.GetComponentInParent<TreeBase>();
            if (comp == null && s_treeSyncType != null) comp = c.GetComponentInParent(s_treeSyncType);
            if (comp == null && s_treeLogType != null) comp = c.GetComponentInParent(s_treeLogType);
            if (comp == null) comp = c.GetComponentInParent<Destructible>();
            if (comp is MonoBehaviour mb && mb is IDestructible) return mb;
            return null;
        }

        private bool IsRecentlyFailedTree(Vector3 p)
        {
            for (int i = 0; i < m_failedTreePos.Count; i++)
                if (Utils.DistanceSqr(m_failedTreePos[i], p) < FailedTreeRadius * FailedTreeRadius)
                    return true;
            return false;
        }

        private void RememberFailedTree(Vector3 p)
        {
            if (IsRecentlyFailedTree(p)) return;
            m_failedTreePos.Add(p);
            if (m_failedTreePos.Count > 16) m_failedTreePos.RemoveAt(0);
        }

        private bool UpdateBreaking(BaseAI ai, float dt)
        {
            // бой важнее ломания
            if (ai.GetTargetCreature() != null)
            {
                m_break = null;
                return false;
            }

            // цель исчезла (повалена) — лучи пересчитаются, маршрут продолжится
            if (m_break == null || m_break.Comp == null || !m_break.Comp)
            {
                Debug.Log("[TrollBuild] Break: target gone, resuming route");
                m_break = null;
                m_lastCast = -1f; // форсируем немедленный пересчёт лучей
                return false;
            }

            // таймаут от последнего НАНЕСЁННОГО удара
            if (Time.time - m_lastLandedSwing > BreakTimeout)
            {
                Debug.Log($"[TrollBuild] Break TIMEOUT on '{m_break.Comp.gameObject.name}' " +
                          $"after {m_swingCount} swings");
                RememberFailedTree(m_break.Pos);
                m_break = null;
                m_steerSide = -m_steerSide; // при простое пробуем другую сторону
                m_lastCast = -1f;
                return false;
            }

            Vector3 pos = ai.transform.position;
            Vector3 to = m_break.Pos - pos;
            to.y = 0f;
            float d = to.magnitude;

            if (d > BreakSwingRange * 0.85f && d > 0.05f)
                ai.MoveTowards(to / d, false);

            Character ch = m_owner.TrollCharacter;
            if (ch != null && d <= BreakSwingRange && Time.time >= m_nextSwing)
            {
                m_nextSwing = Time.time + BreakSwingInterval;

                bool visual = false;
                if (!ch.InAttack())
                {
                    try { visual = ch.StartAttack(null, false); } catch { }
                }

                m_swingCount++;
                m_lastLandedSwing = Time.time;
                ApplyBreakDamage();
                Debug.Log($"[TrollBuild] Break swing #{m_swingCount} on " +
                          $"'{m_break.Comp.gameObject.name}' (visual={visual})");
            }
            return false;
        }

        private void ApplyBreakDamage()
        {
            if (m_break == null || m_break.Destructible == null) return;
            try
            {
                HitData hit = new HitData();
                hit.m_damage.m_damage = BreakDamage;
                hit.m_toolTier = BreakToolTier;
                hit.m_point = m_break.Pos + Vector3.up;
                hit.m_dir = Vector3.up;
                hit.m_hitType = HitData.HitType.EnemyHit;
                m_break.Destructible.Damage(hit);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] Break damage failed: " + e.Message);
            }
        }

        // ====================================================================
        //  Простой: сообщение ТОЛЬКО по факту стояния; смена стороны объезда
        // ====================================================================
        private void TrackStall(Vector3 pos, float dt)
        {
            if (Utils.DistanceXZ(pos, m_lastStallPos) < 0.15f)
            {
                m_stallTimer += dt;

                // раз в StallSideFlip секунд простоя — пробуем другую сторону
                if (m_stallTimer - m_lastFlipMark > StallSideFlip)
                {
                    m_lastFlipMark = m_stallTimer;
                    m_steerSide = -m_steerSide;
                    m_lastCast = -1f; // немедленный пересчёт лучей с новой стороны
                }

                if (m_stallTimer > StallMsgTime)
                {
                    m_stallTimer = 0f;
                    m_lastFlipMark = 0f;
                    NotifyBlocked();
                }
            }
            else
            {
                m_stallTimer = 0f;
                m_lastFlipMark = 0f;
                m_lastStallPos = pos;
            }
        }

        private void NotifyBlocked()
        {
            if (Time.time - m_lastBlockedMsg < 15f) return;
            m_lastBlockedMsg = Time.time;
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                TrollWalkLoc.T("The troll cannot squeeze through with its platform",
                    "Тролль не может протиснуться с платформой"), 0, null, false);
        }

        // ====================================================================
        //  ВЫПЕЧЕННЫЙ ГАБАРИТ (ширина луча + контур для гейта поворота)
        // ====================================================================
        private void BakeFootprintIfNeeded()
        {
            if (m_footVersion == m_owner.PiecesVersion && m_footPoints.Count > 0) return;
            m_footVersion = m_owner.PiecesVersion;

            Transform t = m_owner.transform;
            bool any = false;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            Physics.SyncTransforms(); // защита от протухших bounds

            foreach (var pv in m_owner.AttachedPieces)
            {
                if (pv == null) continue;
                foreach (var c in pv.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled) continue;
                    Bounds b = c.bounds;

                    if ((b.center - t.position).sqrMagnitude > FootGarbageDist * FootGarbageDist)
                        continue;

                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = new Vector3(
                            (i & 1) == 0 ? b.min.x : b.max.x,
                            (i & 2) == 0 ? b.min.y : b.max.y,
                            (i & 4) == 0 ? b.min.z : b.max.z);
                        Vector3 l = t.InverseTransformPoint(corner);
                        if (l.x < minX) minX = l.x;
                        if (l.x > maxX) maxX = l.x;
                        if (l.y < minY) minY = l.y;
                        if (l.y > maxY) maxY = l.y;
                        if (l.z < minZ) minZ = l.z;
                        if (l.z > maxZ) maxZ = l.z;
                    }
                    any = true;
                }
            }

            if (!any)
            {
                minX = -0.9f; maxX = 0.9f;
                minZ = -0.9f; maxZ = 0.9f;
                minY = 0.3f; maxY = 4.2f;
            }

            float cx = Mathf.Clamp((minX + maxX) * 0.5f, -FootMaxCenterOffset, FootMaxCenterOffset);
            float cz = Mathf.Clamp((minZ + maxZ) * 0.5f, -FootMaxCenterOffset, FootMaxCenterOffset);
            float hx = Mathf.Min((maxX - minX) * 0.5f, FootMaxHalfExtent);
            float hz = Mathf.Min((maxZ - minZ) * 0.5f, FootMaxHalfExtent);

            // клиренс: ниже StepClearance лучи не смотрят никогда
            m_spanMin = Mathf.Max(minY, StepClearance);
            m_spanMax = Mathf.Max(maxY, m_spanMin + 0.6f);
            m_spanMid = (m_spanMin + m_spanMax) * 0.5f;
            m_spanHalf = (m_spanMax - m_spanMin) * 0.5f;

            m_boxHalfX = hx;

            m_footRadius = Mathf.Min(
                new Vector2(cx, cz).magnitude + Mathf.Sqrt(hx * hx + hz * hz) + 0.15f,
                FootMaxRadius);

            if (m_spanMax - m_spanMin > 1.8f)
                m_footHeights = new[] { m_spanMin + 0.5f, m_spanMid };
            else
                m_footHeights = new[] { m_spanMid };

            m_footPoints.Clear();
            float step = 0.85f;
            AddEdgePoints(m_footPoints, cx - hx, cx + hx, cz - hz, step, true);
            AddEdgePoints(m_footPoints, cx - hx, cx + hx, cz + hz, step, true);
            AddEdgePoints(m_footPoints, cz - hz, cz + hz, cx - hx, step, false);
            AddEdgePoints(m_footPoints, cz - hz, cz + hz, cx + hx, step, false);
            m_footPoints.Add(new Vector2(cx - hx, cz - hz));
            m_footPoints.Add(new Vector2(cx + hx, cz - hz));
            m_footPoints.Add(new Vector2(cx - hx, cz + hz));
            m_footPoints.Add(new Vector2(cx + hx, cz + hz));
        }

        private static void AddEdgePoints(List<Vector2> pts, float from, float to, float fixedV, float step, bool xAxis)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt((to - from) / step));
            for (int i = 0; i <= n; i++)
            {
                float v = from + (to - from) * i / n;
                pts.Add(xAxis ? new Vector2(v, fixedV) : new Vector2(fixedV, v));
            }
        }

        private static float GroundY(Vector3 p)
        {
            if (ZoneSystem.instance != null)
            {
                try
                {
                    float gh = ZoneSystem.instance.GetGroundHeight(p);
                    if (gh > 0f) return gh;
                }
                catch { }
            }
            return p.y;
        }

        private bool IsForeign(Collider c)
        {
            if (c == null || c.isTrigger) return false;
            if (c.transform.IsChildOf(m_owner.transform)) return false;
            Rigidbody ownBody = m_owner.TrollRigidbody;
            if (ownBody != null && c.attachedRigidbody == ownBody) return false;
            if (c.GetComponentInParent<TrollPieceTag>() != null) return false;
            return true;
        }

        private bool AnyForeign(Vector3 pos, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, radius, s_hits, ObstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (IsForeign(s_hits[i])) return true;
            return false;
        }

        // ====================================================================
        //  Гейт поворота (контур из выпечки; работает во всех режимах)
        // ====================================================================
        public bool HasNearbyForeign()
        {
            BakeFootprintIfNeeded();
            if (m_footRadius <= 0f) return false;
            Vector3 pos = m_owner.transform.position;
            return AnyForeign(new Vector3(pos.x, pos.y + m_spanMid, pos.z), m_footRadius + 1f);
        }

        public bool RotationStepBlocked(Vector3 pos, Quaternion candidate)
        {
            BakeFootprintIfNeeded();
            foreach (Vector2 lp in m_footPoints)
            {
                Vector3 off = candidate * new Vector3(lp.x, 0f, lp.y);
                float px = pos.x + off.x, pz = pos.z + off.z;
                foreach (float h in m_footHeights)
                {
                    if (AnyForeign(new Vector3(px, pos.y + h, pz), DetailProbeRadius))
                        return true;
                }
            }
            return false;
        }
    }

    public class TrollPiecesContainer : MonoBehaviour
    {
        public Character TrollCharacter { get; private set; }
        public string TrollUUID { get; private set; } = "";

        private Transform m_buildRoot;
        public Transform BuildRoot { get { EnsureBuildRoot(); return m_buildRoot; } }

        private readonly List<ZNetView> m_attachedPieces = new List<ZNetView>();
        public List<ZNetView> AttachedPieces => m_attachedPieces;
        public int PieceCount => m_attachedPieces.Count;

        public Vector3 PlatformVelocity { get; private set; }
        public Vector3 PlatformAngularVelocity { get; private set; }
        private Vector3 m_prevPos;
        private Quaternion m_prevRot;
        private bool m_hasPrev;

        public readonly List<Transform> DeckSnapPoints = new List<Transform>();

        private Collider[] m_trollColliders = Array.Empty<Collider>();

        private Vector2s m_lastSector = new Vector2s(short.MinValue, short.MinValue);
        private Vector3 m_lastSyncedPos = Vector3.zero;
        private Quaternion m_lastSyncedRot = Quaternion.identity;
        private float m_syncTimer;
        private bool m_wasMoving;
        private bool m_isDestroyingPieces;
        private bool m_isRecheckingSupport;

        private float m_boneUpgradeTimer;
        private int m_boneUpgradeAttempts;
        private const int MaxBoneUpgradeAttempts = 60;

        private Rigidbody m_trollBody;
        public Rigidbody TrollRigidbody => m_trollBody;

        private int m_piecesVersion;
        public int PiecesVersion => m_piecesVersion;

        private TrollPlatformNavigator m_navigator;
        public TrollPlatformNavigator Navigator => m_navigator ??= new TrollPlatformNavigator(this);

        public bool IsBreakingPlatform => m_navigator != null && m_navigator.IsBreaking;

        private float m_rotHoldTimer;

        private float m_lastRecoverCheck = -30f;

        private void Awake()
        {
            TrollCharacter = GetComponent<Character>();
            m_trollBody = GetComponent<Rigidbody>();
            m_prevPos = transform.position;
            m_prevRot = transform.rotation;
            CacheTrollColliders();
            if (TrollCharacter != null)
            {
                try { TrollCharacter.m_onDeath = (Action)Delegate.Combine(TrollCharacter.m_onDeath, new Action(OnTrollDeath)); }
                catch { }
            }
        }

        private void Start()
        {
            InitUUID();
            EnsureBuildRoot();
            CacheTrollColliders();
            CreateDeckSnapPoints();
            m_lastSyncedPos = transform.position;
            m_lastSyncedRot = transform.rotation;
            if (ZoneSystem.instance != null) m_lastSector = ZoneSystem.GetZone(transform.position);

            if (!string.IsNullOrEmpty(TrollUUID))
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);
        }

        public void InitUUID()
        {
            if (!string.IsNullOrEmpty(TrollUUID)) return;
            ZNetView nv = GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() != null)
            {
                ZDO zdo = nv.GetZDO();

                string savedUUID = zdo.GetString(TrollBuildConstants.HashTrollUUID, "");
                if (string.IsNullOrEmpty(savedUUID))
                {
                    savedUUID = Guid.NewGuid().ToString();
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    zdo.Set(TrollBuildConstants.HashTrollUUID, savedUUID);
                }

                TrollUUID = savedUUID;
                TrollRegistry.RegisterTroll(TrollUUID, this);
            }
        }

        private Transform FindBuildBone()
        {
            Animator anim = GetComponentInChildren<Animator>();
            if (anim == null) return null;
            HumanBodyBones[] chain =
            {
                HumanBodyBones.Chest, HumanBodyBones.UpperChest,
                HumanBodyBones.Spine, HumanBodyBones.Hips
            };
            foreach (HumanBodyBones b in chain)
            {
                try
                {
                    Transform t = anim.GetBoneTransform(b);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        public void EnsureBuildRoot()
        {
            if (m_buildRoot == null)
            {
                Transform legacy = transform.Find("TrollBuildRoot");
                if (legacy != null) Destroy(legacy.gameObject);

                GameObject obj = new GameObject("TrollBuildRoot");
                Transform parent = FindBuildBone();
                if (parent == null) parent = transform;
                obj.transform.SetParent(parent, false);
                obj.transform.position = transform.TransformPoint(TrollBuildConstants.DefaultBuildRootOffset);
                obj.transform.rotation = transform.rotation;
                m_buildRoot = obj.transform;
            }

            Transform p = m_buildRoot.parent;
            if (p != null)
            {
                Vector3 s = p.lossyScale;
                if (Mathf.Abs(s.x) > 0.001f && Mathf.Abs(s.y) > 0.001f && Mathf.Abs(s.z) > 0.001f)
                {
                    Vector3 target = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
                    if ((m_buildRoot.localScale - target).sqrMagnitude > 1e-6f)
                        m_buildRoot.localScale = target;
                }
            }
        }

        private bool TryUpgradeBuildRootToBone()
        {
            if (m_buildRoot == null) return false;
            if (m_buildRoot.parent != transform) return true;

            Transform bone = FindBuildBone();
            if (bone == null || bone == transform) return false;

            m_buildRoot.SetParent(bone, true);
            EnsureBuildRoot();
            return true;
        }

        public bool IsInBuildZone(Vector3 worldPoint, float mult = 1f)
        {
            Transform root = BuildRoot;
            if (root == null) return false;
            Vector3 lp = root.InverseTransformPoint(worldPoint);
            Vector3 he = TrollBuildConstants.BuildZoneHalfExtents * mult;
            return Mathf.Abs(lp.x) <= he.x && Mathf.Abs(lp.y) <= he.y && Mathf.Abs(lp.z) <= he.z;
        }

        public void CreateDeckSnapPoints()
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            Transform root = BuildRoot;
            if (root == null) return;

            Transform snapRoot = root.Find("_DeckSnapGrid");
            if (snapRoot == null)
            {
                GameObject gridObj = new GameObject("_DeckSnapGrid");
                gridObj.transform.SetParent(root, false);
                snapRoot = gridObj.transform;

                float hx = Mathf.Max(0.5f, TrollBuildConstants.BuildZoneHalfExtents.x - 0.25f);
                float hz = Mathf.Max(0.5f, TrollBuildConstants.BuildZoneHalfExtents.z - 0.25f);
                for (float z = -hz; z <= hz + 0.01f; z += 1f)
                    for (float x = -hx; x <= hx + 0.01f; x += 1f)
                    {
                        GameObject snap = new GameObject("snappoint");
                        snap.tag = "snappoint";
                        snap.transform.SetParent(snapRoot, false);
                        snap.transform.localPosition = new Vector3(Mathf.Round(x * 2f) * 0.5f, 0f, Mathf.Round(z * 2f) * 0.5f);
                        DeckSnapPoints.Add(snap.transform);
                    }
            }
            else
            {
                DeckSnapPoints.Clear();
                foreach (Transform child in snapRoot)
                    if (child.CompareTag("snappoint")) DeckSnapPoints.Add(child);
            }
        }

        public void CacheTrollColliders()
        {
            m_trollColliders = GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.GetComponentInParent<TrollPieceTag>() == null)
                .ToArray();
        }

        public void RegisterPiece(ZNetView pieceView, Vector3 localPos, Quaternion localRot)
        {
            if (pieceView == null) return;
            EnsureBuildRoot();

            if (!m_attachedPieces.Contains(pieceView)) m_attachedPieces.Add(pieceView);

            if (pieceView.GetComponent<TeleportWorld>() != null)
            {
                foreach (MeshCollider mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
                {
                    mc.enabled = false;
                }

                Transform existingFrame = pieceView.transform.Find("_PortalFrameColliders");
                if (existingFrame == null)
                {
                    GameObject frameObj = new GameObject("_PortalFrameColliders");
                    frameObj.transform.SetParent(pieceView.transform, false);
                    frameObj.layer = pieceView.gameObject.layer;

                    BoxCollider leftPost = frameObj.AddComponent<BoxCollider>();
                    leftPost.center = new Vector3(-0.75f, 1.2f, 0f);
                    leftPost.size = new Vector3(0.45f, 2.4f, 0.5f);

                    BoxCollider rightPost = frameObj.AddComponent<BoxCollider>();
                    rightPost.center = new Vector3(0.75f, 1.2f, 0f);
                    rightPost.size = new Vector3(0.45f, 2.4f, 0.5f);

                    BoxCollider topBar = frameObj.AddComponent<BoxCollider>();
                    topBar.center = new Vector3(0f, 2.5f, 0f);
                    topBar.size = new Vector3(1.9f, 0.5f, 0.5f);
                }
            }
            else
            {
                foreach (MeshCollider mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (!mc.convex) mc.convex = true;
                }
            }

            pieceView.transform.SetParent(m_buildRoot, false);
            pieceView.transform.localPosition = localPos;
            pieceView.transform.localRotation = localRot;
            pieceView.transform.localScale = Vector3.one;

            TrollPieceTag tag = pieceView.GetComponent<TrollPieceTag>();
            if (tag == null) tag = pieceView.gameObject.AddComponent<TrollPieceTag>();
            tag.Container = this;
            tag.AttachedAt = Time.time; // FIX 2

            Rigidbody trollBody = GetComponent<Rigidbody>();
            if (trollBody != null)
            {
                trollBody.centerOfMass = trollBody.centerOfMass;
                if (trollBody.inertiaTensor != Vector3.zero)
                {
                    trollBody.inertiaTensor = trollBody.inertiaTensor;
                    trollBody.inertiaTensorRotation = trollBody.inertiaTensorRotation;
                }
            }

            WearNTear wnt = pieceView.GetComponent<WearNTear>();
            if (wnt != null)
            {
                wnt.m_staticPosition = false;
                wnt.m_noSupportWear = true;
            }

            ZSyncTransform sync = pieceView.GetComponent<ZSyncTransform>();
            if (sync != null) sync.enabled = false;
            foreach (TerrainModifier tm in pieceView.GetComponentsInChildren<TerrainModifier>(true)) { tm.enabled = false; Destroy(tm); }
            foreach (TerrainOp op in pieceView.GetComponentsInChildren<TerrainOp>(true)) { op.enabled = false; Destroy(op); }

            IgnoreCollisionsWithTrollBody(pieceView.gameObject);

            TrollMaterialFixer.FixPieceMaterials(pieceView);
            if (!TrollBuildConstants.IsDedicatedServer)
            {
                foreach (Renderer r in pieceView.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.GetComponentInParent<Switch>() != null) continue;
                    r.probeAnchor = transform;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
            }

            if (pieceView.GetZDO() != null)
            {
                pieceView.GetZDO().Set(TrollBuildConstants.HashTrollUUID, TrollUUID);
                TrollPieceZdoHelper.MovePieceZDO(pieceView.GetZDO(), pieceView.transform.position, pieceView.transform.rotation);
            }

            m_piecesVersion++;
            m_navigator?.Invalidate();

            Debug.Log($"[TrollBuild] Piece '{pieceView.gameObject.name}' registered on troll {TrollUUID}, worldPos={pieceView.transform.position.ToString("F1")} (total {m_attachedPieces.Count})");

            RecheckAllPiecesSupport(false);
        }

        public void IgnoreCollisionsWithTrollBody(GameObject pieceObj)
        {
            if (m_trollColliders == null || m_trollColliders.Length == 0) CacheTrollColliders();
            Collider[] pieceCols = pieceObj.GetComponentsInChildren<Collider>(true);
            foreach (Collider pc in pieceCols)
            {
                if (pc == null || pc.isTrigger) continue;
                foreach (Collider tc in m_trollColliders)
                    if (tc != null && tc != pc) Physics.IgnoreCollision(tc, pc, true);
            }
        }

        private void FixedUpdate()
        {
            ZNetView nv = GetComponent<ZNetView>();
            if (nv == null || nv.GetZDO() == null) return;

            if (string.IsNullOrEmpty(TrollUUID)) InitUUID();
            EnsureBuildRoot();

            // FIX 13: восстановление потерянной платформы
            try { UpdatePlatformRecovery(); }
            catch (Exception e) { Debug.LogWarning("[TrollBuild] Platform recovery failed: " + e.Message); }

            if (m_buildRoot != null && m_buildRoot.parent == transform && m_boneUpgradeAttempts < MaxBoneUpgradeAttempts)
            {
                m_boneUpgradeTimer -= Time.fixedDeltaTime;
                if (m_boneUpgradeTimer <= 0f)
                {
                    m_boneUpgradeTimer = 1f;
                    m_boneUpgradeAttempts++;
                    TryUpgradeBuildRootToBone();
                }
            }

            if (!string.IsNullOrEmpty(TrollUUID) && TrollPieceAttachmentQueue.HasPending(TrollUUID))
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);

            float dt = Time.fixedDeltaTime;
            Vector3 pos = m_buildRoot != null ? m_buildRoot.position : transform.position;
            Quaternion rot = m_buildRoot != null ? m_buildRoot.rotation : transform.rotation;
            if (m_hasPrev && dt > 0f)
            {
                Vector3 dp = pos - m_prevPos;
                if (dp.sqrMagnitude < 100f)
                {
                    PlatformVelocity = dp / dt;
                    Quaternion dq = rot * Quaternion.Inverse(m_prevRot);
                    dq.ToAngleAxis(out float angDeg, out Vector3 axis);
                    if (angDeg > 180f) angDeg -= 360f;
                    if (axis.sqrMagnitude > 1e-10f && Mathf.Abs(angDeg) > 0.001f)
                        PlatformAngularVelocity = axis.normalized * (angDeg * Mathf.Deg2Rad / dt);
                    else
                        PlatformAngularVelocity = Vector3.zero;
                }
                else
                {
                    PlatformVelocity = Vector3.zero;
                    PlatformAngularVelocity = Vector3.zero;
                }
            }
            m_prevPos = pos; m_prevRot = rot; m_hasPrev = true;

            float moveDelta = Vector3.Distance(pos, m_lastSyncedPos);
            float rotDelta = Quaternion.Angle(rot, m_lastSyncedRot);
            bool moving = moveDelta > 0.05f || rotDelta > 0.5f;

            if (moving)
            {
                m_wasMoving = true;
                if (ZoneSystem.instance != null)
                {
                    Vector2s sector = ZoneSystem.GetZone(pos);
                    if (sector != m_lastSector || moveDelta > 10f)
                    {
                        m_lastSector = sector;
                        m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                        SyncPiecesWorldZDO();
                    }
                }
            }
            else if (m_wasMoving)
            {
                m_wasMoving = false;
                m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                if (ZoneSystem.instance != null) m_lastSector = ZoneSystem.GetZone(pos);
                SyncPiecesWorldZDO();
            }

            m_syncTimer += dt;
            if (m_syncTimer >= 0.5f)
            {
                m_syncTimer = 0f;
                if (moving)
                {
                    m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                    SyncPiecesWorldZDO();
                }
            }
        }

        // ================================================================
        //  FIX 13: восстановление платформы
        // ================================================================
        private void UpdatePlatformRecovery()
        {
            if (string.IsNullOrEmpty(TrollUUID)) return;
            if (ZNetScene.instance == null || ZDOMan.instance == null || ZNet.instance == null) return;
            if (Time.time - m_lastRecoverCheck < 5f) return;
            m_lastRecoverCheck = Time.time;

            List<ZDO> pieces = TrollPieceZdoIndex.GetPieces(TrollUUID);
            if (pieces == null || pieces.Count == 0) return;

            pieces.RemoveAll(z => z == null || !z.IsValid());
            if (pieces.Count == 0) return;

            ZNetView trollNv = GetComponent<ZNetView>();
            if (trollNv == null || !trollNv.IsValid()) return;
            if (!ZNet.instance.IsServer() && !trollNv.IsOwner()) return;

            Quaternion trollRot = transform.rotation;
            Vector3 rootPos = transform.TransformPoint(TrollBuildConstants.DefaultBuildRootOffset);

            int moved = 0;
            foreach (ZDO zdo in pieces)
            {
                bool attached = false;
                foreach (var pv in m_attachedPieces)
                    if (pv != null && pv.IsValid() && pv.GetZDO() == zdo) { attached = true; break; }
                if (attached) continue;
                if (ZNetScene.instance.FindInstance(zdo) != null) continue;

                Vector3 target = rootPos + trollRot * zdo.GetVec3(TrollBuildConstants.HashLocalPos, Vector3.zero);
                if ((zdo.GetPosition() - target).sqrMagnitude <= 4f) continue;

                if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
                TrollPieceZdoHelper.MovePieceZDO(zdo, target,
                    trollRot * zdo.GetQuaternion(TrollBuildConstants.HashLocalRotQ, Quaternion.identity));
                moved++;
            }

            if (moved > 0)
                Debug.Log($"[TrollBuild] Platform RECOVERY: teleported {moved} left-behind piece(s) to troll {TrollUUID} — they will attach when their zone loads");
            else if (m_attachedPieces.Count == 0)
                Debug.Log($"[TrollBuild] Platform check: {pieces.Count} piece ZDO(s) belong to troll {TrollUUID}, none attached yet — waiting for zones/attach");
        }

        public void SyncPiecesWorldZDO()
        {
            ZNetView trollNv = GetComponent<ZNetView>();
            bool isTrollOwner = trollNv != null && trollNv.IsValid() && trollNv.IsOwner();

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                var piece = m_attachedPieces[i];
                if (piece == null || !piece.IsValid() || piece.GetZDO() == null)
                {
                    m_attachedPieces.RemoveAt(i);
                    continue;
                }

                if (isTrollOwner && !piece.IsOwner())
                    piece.ClaimOwnership();

                if (piece.IsOwner())
                {
                    Vector3 curPos = piece.transform.position;
                    Quaternion curRot = piece.transform.rotation;
                    if ((piece.GetZDO().GetPosition() - curPos).sqrMagnitude > 0.001f)
                    {
                        TrollPieceZdoHelper.MovePieceZDO(piece.GetZDO(), curPos, curRot);
                    }
                }
            }
        }

        public void OnPieceDestroyed(WearNTear wnt)
        {
            if (wnt == null) return;
            ZNetView nv = wnt.GetComponent<ZNetView>();
            if (nv != null) m_attachedPieces.Remove(nv);
            m_attachedPieces.RemoveAll(p => p == null || !p.IsValid());

            m_piecesVersion++;
            m_navigator?.Invalidate();

            bool tearingDown = ZNetScene.instance == null || !ZNetScene.instance.enabled || ZNet.instance == null;
            if (!tearingDown)
                RecheckAllPiecesSupport(true);
        }

        public void RecheckAllPiecesSupport(bool triggerDestruction = true)
        {
            if (m_attachedPieces.Count == 0 || m_isRecheckingSupport) return;
            m_isRecheckingSupport = true;
            try
            {
                m_attachedPieces.RemoveAll(p => p == null || !p.IsValid());
                var sorted = m_attachedPieces.ToList();
                sorted.Sort((a, b) => a.transform.localPosition.y.CompareTo(b.transform.localPosition.y));
                foreach (var v in sorted)
                {
                    WearNTear w = v != null ? v.GetComponent<WearNTear>() : null;
                    if (w != null) TrollBuildSupport.CalculateTrollPieceSupport(w);
                }
            }
            finally { m_isRecheckingSupport = false; }
        }

        public void OnTrollDeath()
        {
            if (m_isDestroyingPieces) return;
            m_isDestroyingPieces = true;

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                ZNetView v = m_attachedPieces[i];
                if (v == null || !v.IsValid()) continue;
                v.GetZDO().Set(TrollBuildConstants.HashTrollUUID, "");
                Piece p = v.GetComponent<Piece>();
                if (p != null) p.DropResources(null);
                v.Destroy();
            }
            m_attachedPieces.Clear();
            TrollPieceAttachmentQueue.ClearQueueForTroll(TrollUUID);
        }

        private void OnDestroy()
        {
            TrollRegistry.UnregisterTroll(TrollUUID);
            if (ZNetScene.instance == null || ZNet.instance == null) return;

            if (TrollCharacter != null && TrollCharacter.IsDead())
            {
                OnTrollDeath();
                return;
            }

            SyncPiecesWorldZDO();
            for (int i = 0; i < m_attachedPieces.Count; i++)
            {
                var piece = m_attachedPieces[i];
                if (piece != null) piece.transform.SetParent(null);
            }
        }

        // ================================================================
        //  Гейт поворота (работает во всех режимах при наличии построек)
        // ================================================================
        public void LimitTurnSpeedForPlatform(ref float turnSpeed, float dt)
        {
            if (m_attachedPieces.Count == 0 || turnSpeed <= 0f || dt <= 0f) return;
            if (TrollCharacter == null) return;
            if (IsBreakingPlatform) return;

            var nav = Navigator;
            if (nav.FootprintRadius <= 0f) return;

            if (!nav.HasNearbyForeign()) { m_rotHoldTimer = 0f; return; }

            Quaternion current = transform.rotation;
            Quaternion target = TrollCharacter.GetLookYaw();
            if (Quaternion.Angle(current, target) < 0.1f) { m_rotHoldTimer = 0f; return; }

            float stepDeg = turnSpeed * dt;
            if (stepDeg < 0.1f) return;

            Vector3 pos = transform.position;

            if (!nav.RotationStepBlocked(pos, Quaternion.RotateTowards(current, target, stepDeg)))
            { m_rotHoldTimer = 0f; return; }

            if (!nav.RotationStepBlocked(pos, Quaternion.RotateTowards(current, target, stepDeg * 0.5f)))
            { turnSpeed *= 0.5f; m_rotHoldTimer = 0f; return; }

            if (!nav.RotationStepBlocked(pos, Quaternion.RotateTowards(current, target, stepDeg * 0.25f)))
            { turnSpeed *= 0.25f; m_rotHoldTimer = 0f; return; }

            m_rotHoldTimer += dt;
            if (m_rotHoldTimer < TrollBuildConstants.RotationHoldMaxTime)
            {
                turnSpeed = 0f;
                return;
            }
        }
    }

    public static class TrollRegistry
    {
        private static readonly Dictionary<string, TrollPiecesContainer> s_trolls = new Dictionary<string, TrollPiecesContainer>();

        public static void RegisterTroll(string uuid, TrollPiecesContainer c)
        {
            if (!string.IsNullOrEmpty(uuid) && c != null) s_trolls[uuid] = c;
        }

        public static void UnregisterTroll(string uuid)
        {
            if (!string.IsNullOrEmpty(uuid)) s_trolls.Remove(uuid);
        }

        public static bool TryGetTroll(string uuid, out TrollPiecesContainer c)
        {
            c = null;
            if (string.IsNullOrEmpty(uuid)) return false;
            return s_trolls.TryGetValue(uuid, out c) && c != null;
        }
    }

    public static class TrollPieceZdoIndex
    {
        private static readonly Dictionary<string, List<ZDO>> s_byTroll =
            new Dictionary<string, List<ZDO>>();

        public static void Index(ZDO zdo)
        {
            try
            {
                if (zdo == null || zdo.m_uid == ZDOID.None) return;
                string guid = zdo.GetString(TrollBuildConstants.HashTrollUUID, "");
                if (string.IsNullOrEmpty(guid)) return;

                if (!zdo.GetBool(TrollBuildConstants.HashHasLocal, false)) return; // FIX 4

                if (!s_byTroll.TryGetValue(guid, out List<ZDO> list))
                {
                    list = new List<ZDO>();
                    s_byTroll[guid] = list;
                }
                if (!list.Contains(zdo)) list.Add(zdo);
            }
            catch { }
        }

        public static List<ZDO> GetPieces(string trollGuid)
        {
            return string.IsNullOrEmpty(trollGuid) || !s_byTroll.TryGetValue(trollGuid, out List<ZDO> list)
                ? null : list;
        }

        public static void Clear() => s_byTroll.Clear();
    }

    [HarmonyPatch]
    public static class TrollPieceZdoIndexPatches
    {
        [HarmonyPatch(typeof(ZDO), "Load")]
        [HarmonyPostfix]
        private static void ZDO_Load_Postfix(ZDO __instance) => TrollPieceZdoIndex.Index(__instance);

        [HarmonyPatch(typeof(ZDO), "Deserialize")]
        [HarmonyPostfix]
        private static void ZDO_Deserialize_Postfix(ZDO __instance) => TrollPieceZdoIndex.Index(__instance);
    }

    public static class TrollPieceAttachmentQueue
    {
        private static readonly Dictionary<string, List<ZNetView>> s_pending = new Dictionary<string, List<ZNetView>>();
        private static readonly Quaternion Sentinel = new Quaternion(-1f, 0f, 0f, 0f);

        public static void Enqueue(string uuid, ZNetView v)
        {
            if (string.IsNullOrEmpty(uuid) || v == null) return;
            if (!s_pending.TryGetValue(uuid, out var list)) { list = new List<ZNetView>(); s_pending[uuid] = list; }
            if (!list.Contains(v)) list.Add(v);

            try { if (v.gameObject.activeSelf) v.gameObject.SetActive(false); } catch { }

            Debug.Log($"[TrollBuild] Piece '{v.gameObject.name}' waiting for troll {uuid} — GO hidden at {v.transform.position.ToString("F1")}");
        }

        public static bool HasPending(string uuid)
        {
            return !string.IsNullOrEmpty(uuid) && s_pending.TryGetValue(uuid, out var list) && list.Count > 0;
        }

        public static void ClearQueueForTroll(string uuid)
        {
            if (!string.IsNullOrEmpty(uuid)) s_pending.Remove(uuid);
        }

        public static List<ZNetView> TakePending(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            if (s_pending.TryGetValue(uuid, out List<ZNetView> list))
            {
                s_pending.Remove(uuid);
                return list;
            }
            return null;
        }

        public static List<ZNetView> GetPendingViews(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            return s_pending.TryGetValue(uuid, out var list) ? list : null;
        }

        public static void CheckPending(string uuid, TrollPiecesContainer container)
        {
            if (string.IsNullOrEmpty(uuid) || container == null) return;
            if (!s_pending.TryGetValue(uuid, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var v = list[i];
                if (v == null || !v.IsValid()) { list.RemoveAt(i); continue; }
                Attach(container, v);
                Debug.Log($"[TrollBuild] Pending piece attached: '{v.gameObject.name}' -> troll {uuid}");
                list.RemoveAt(i);
            }
            if (list.Count == 0) s_pending.Remove(uuid);
        }

        public static void Attach(TrollPiecesContainer container, ZNetView pieceView)
        {
            if (!container || !pieceView || !pieceView.IsValid()) return;

            if (pieceView.GetComponent<Character>() != null) return; // FIX 5

            try { if (!pieceView.gameObject.activeSelf) pieceView.gameObject.SetActive(true); } catch { }
            container.EnsureBuildRoot();
            ZDO zdo = pieceView.GetZDO();
            if (zdo == null) return;

            Vector3 lp;
            Quaternion lr;
            bool hasLocal = zdo.GetBool(TrollBuildConstants.HashHasLocal, false);
            Quaternion q = hasLocal ? zdo.GetQuaternion(TrollBuildConstants.HashLocalRotQ, Sentinel) : Sentinel;

            if (q != Sentinel)
            {
                lp = zdo.GetVec3(TrollBuildConstants.HashLocalPos, Vector3.zero);
                lr = q;
            }
            else
            {
                Transform root = container.BuildRoot;
                lp = root.InverseTransformPoint(pieceView.transform.position);
                lr = Quaternion.Inverse(root.rotation) * pieceView.transform.rotation;
                if (pieceView.IsOwner())
                {
                    zdo.Set(TrollBuildConstants.HashTrollUUID, container.TrollUUID);
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);
                    zdo.Set(TrollBuildConstants.HashLocalPos, lp);
                    zdo.Set(TrollBuildConstants.HashLocalRotQ, lr);
                }
            }
            container.RegisterPiece(pieceView, lp, lr);
        }
    }

    public static class TrollBuildSupport
    {
        private static readonly AccessTools.FieldRef<WearNTear, float> SupportField =
            AccessTools.FieldRefAccess<WearNTear, float>("m_support");

        private static readonly int StaticMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain", "piece");
        private static readonly Collider[] s_overlap = new Collider[64];
        private static readonly List<Vector3> s_points = new List<Vector3>();
        private static readonly List<float> s_pointValues = new List<float>();

        public static void CalculateTrollPieceSupport(WearNTear wnt)
        {
            if (wnt == null) return;
            ZNetView nv = wnt.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            TrollPieceTag tag = wnt.GetComponent<TrollPieceTag>();
            if (tag == null || tag.Container == null) return;

            GetMaterialProperties(wnt.m_materialType, out float max, out _, out float vLoss, out float hLoss);

            float support;
            ZDOID parentId = nv.GetZDO().GetZDOID(TrollBuildConstants.ParentPieceHashPair);
            if (parentId == ZDOID.None)
            {
                support = max;
            }
            else
            {
                support = ComputeFromSiblings(wnt, tag.Container, max, vLoss, hLoss);
            }

            support = Mathf.Clamp(support, 0f, max);
            SupportField(wnt) = support;
            if (nv.IsOwner()) nv.GetZDO().Set(ZDOVars.s_support, support);
        }

        private static float ComputeFromSiblings(WearNTear wnt, TrollPiecesContainer container, float max, float vLoss, float hLoss)
        {
            Vector3 com = wnt.transform.position + wnt.transform.rotation * wnt.m_comOffset;
            float best = 0f;

            if (TouchesStaticWorld(wnt)) return max;

            s_points.Clear();
            s_pointValues.Clear();

            foreach (ZNetView siblingView in container.AttachedPieces)
            {
                if (siblingView == null || !siblingView.IsValid()) continue;
                WearNTear other = siblingView.GetComponent<WearNTear>();
                if (other == null || other == wnt || !other.m_supports) continue;

                float otherSupport = ReadSupport(other);
                if (otherSupport <= 0f) continue;

                Vector3 otherCom = other.transform.position + other.transform.rotation * other.m_comOffset;
                float dist = Vector3.Distance(com, otherCom) + 0.1f;
                float dist2 = Vector3.Distance(com, other.transform.position) + 0.1f;
                if (dist2 < dist && !other.m_forceCorrectCOMCalculation && !wnt.m_forceCorrectCOMCalculation) dist = dist2;

                best = Mathf.Max(best, otherSupport - vLoss * dist * otherSupport);

                Vector3 sp = FindSupportPoint(com, other);
                if (sp.y < com.y + 0.05f)
                {
                    Vector3 dir = sp - com;
                    if (dir.sqrMagnitude > 1e-6f)
                    {
                        dir.Normalize();
                        if (dir.y < 0f)
                        {
                            float t = Mathf.Acos(1f - Mathf.Abs(dir.y)) / 1.5707964f;
                            float loss = Mathf.Lerp(vLoss, hLoss, t);
                            best = Mathf.Max(best, otherSupport - loss * dist * otherSupport);
                        }
                    }
                    s_points.Add(sp);
                    s_pointValues.Add(otherSupport - hLoss * dist * otherSupport);
                }
            }

            for (int i = 0; i < s_points.Count - 1; i++)
            {
                Vector3 a = s_points[i] - com; a.y = 0f;
                for (int j = i + 1; j < s_points.Count; j++)
                {
                    float avg = (s_pointValues[i] + s_pointValues[j]) * 0.5f;
                    if (avg > best)
                    {
                        Vector3 b = s_points[j] - com; b.y = 0f;
                        if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f && Vector3.Angle(a, b) >= 100f)
                            best = avg;
                    }
                }
            }
            return best;
        }

        private static bool TouchesStaticWorld(WearNTear wnt)
        {
            Bounds bounds = default;
            bool has = false;
            foreach (Collider c in wnt.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (!has) { bounds = c.bounds; has = true; }
                else bounds.Encapsulate(c.bounds);
            }
            if (!has) return false;
            bounds.Expand(0.3f);

            int n = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, s_overlap, Quaternion.identity, StaticMask);
            for (int i = 0; i < n; i++)
            {
                Collider c = s_overlap[i];
                if (c == null || c.isTrigger) continue;
                if (c.GetComponentInParent<TrollPieceTag>() != null) continue;
                if (c.GetComponentInParent<TrollPiecesContainer>() != null) continue;
                WearNTear other = c.GetComponentInParent<WearNTear>();
                if (other == wnt) continue;
                if (other != null && c.attachedRigidbody != null) continue;
                return true;
            }
            return false;
        }

        private static Vector3 FindSupportPoint(Vector3 com, WearNTear other)
        {
            Vector3 best = other.transform.position;
            float bestDist = float.MaxValue;
            foreach (Collider c in other.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                MeshCollider mc = c as MeshCollider;
                if (mc != null && !mc.convex) continue;
                Vector3 p = c.ClosestPoint(com);
                float d = (p - com).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private static float ReadSupport(WearNTear other)
        {
            ZNetView nv = other.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return 0f;
            if (nv.IsOwner()) return SupportField(other);
            GetMaterialProperties(other.m_materialType, out float max, out _, out _, out _);
            return nv.GetZDO().GetFloat(ZDOVars.s_support, max);
        }

        public static void GetMaterialProperties(WearNTear.MaterialType type,
            out float maxSupport, out float minSupport, out float verticalLoss, out float horizontalLoss)
        {
            switch (type)
            {
                case WearNTear.MaterialType.Wood: maxSupport = 100f; minSupport = 10f; verticalLoss = 0.125f; horizontalLoss = 0.2f; return;
                case WearNTear.MaterialType.Stone: maxSupport = 1000f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 1f; return;
                case WearNTear.MaterialType.Iron: maxSupport = 1500f; minSupport = 20f; verticalLoss = 0.07692308f; horizontalLoss = 0.07692308f; return;
                case WearNTear.MaterialType.HardWood: maxSupport = 140f; minSupport = 10f; verticalLoss = 0.1f; horizontalLoss = 0.16666667f; return;
                case WearNTear.MaterialType.Marble: maxSupport = 1500f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 0.5f; return;
                case WearNTear.MaterialType.Ashstone: maxSupport = 2000f; minSupport = 100f; verticalLoss = 0.1f; horizontalLoss = 0.33333334f; return;
                case WearNTear.MaterialType.Ancient: maxSupport = 5000f; minSupport = 100f; verticalLoss = 0.06666667f; horizontalLoss = 0.25f; return;
                case WearNTear.MaterialType.Ice: maxSupport = 1000f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 0.33333334f; return;
                case WearNTear.MaterialType.Timberwood: maxSupport = 200f; minSupport = 10f; verticalLoss = 0.07692308f; horizontalLoss = 0.2f; return;
                default: maxSupport = 0f; minSupport = 0f; verticalLoss = 0f; horizontalLoss = 0f; return;
            }
        }
    }

    [HarmonyPatch]
    public static class TrollBuildingPatches
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRayMaskRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        private static readonly AccessTools.FieldRef<Player, int> PlaceWaterRayMaskRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeWaterRayMask");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRotationRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegreesRef =
            AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");
        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatusRef =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
        private static readonly AccessTools.FieldRef<Player, int> ManualSnapPointRef =
            AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
        private static readonly AccessTools.FieldRef<Player, bool> AltPlaceRef =
            AccessTools.FieldRefAccess<Player, bool>("m_altPlace");
        private static readonly AccessTools.FieldRef<Player, List<Piece>> TempPiecesRef =
            AccessTools.FieldRefAccess<Player, List<Piece>>("m_tempPieces");
        private static readonly AccessTools.FieldRef<Player, RaycastHit[]> RaycastHoverHitsRef =
            AccessTools.FieldRefAccess<Player, RaycastHit[]>("m_raycastHoverHits");
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> LastGroundBodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_lastGroundBody");
        private static readonly AccessTools.FieldRef<Character, Collider> LastGroundColliderRef =
            AccessTools.FieldRefAccess<Character, Collider>("m_lastGroundCollider");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> LastAttachBodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_lastAttachBody");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastAttachPosRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_lastAttachPos");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> BodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");

        private static readonly MethodInfo s_findSnapMethod = AccessTools.Method(typeof(Player), "FindClosestSnapPoints");
        private static readonly MethodInfo s_overlapMethod = AccessTools.Method(typeof(Player), "IsOverlappingOtherPiece");

        private static readonly List<Transform> s_ghostSnapPoints = new List<Transform>();

        private static bool s_loggedPatchActive;
        private static string s_lastTargetLog;
        private static float s_lastTargetLogTime;
        private static float s_lastZoneRejectLog;

        private static bool IsTroll(Character c) =>
            c != null && c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);

        private static void LogTargetChange(string target)
        {
            if (target == s_lastTargetLog && Time.time - s_lastTargetLogTime < 2f) return;
            s_lastTargetLog = target;
            s_lastTargetLogTime = Time.time;
            Debug.Log("[TrollBuild] RayTest target: " + target);
        }

        private static void LogZoneReject(Character troll, Vector3 worldPoint)
        {
            if (Time.time - s_lastZoneRejectLog < 1f) return;
            s_lastZoneRejectLog = Time.time;
            Vector3 local = troll.transform.InverseTransformPoint(worldPoint);
            Debug.Log($"[TrollBuild] Troll hit OUTSIDE build zone: troll-local point = {local.ToString("F2")}. " +
                      "If this repeats where you want to build — tune these constants.");
        }

        private struct GhostFlags
        {
            public bool GroundPiece, GroundOnly, CultivatedGroundOnly, VegetationGroundOnly;
            public bool NotOnWood, NotOnTiltingSurface, NoInWater, NoClipping;
        }
        private static GhostFlags s_savedFlags;
        private static bool s_flagsActive;
        private static Piece s_flagsPiece;

        private static void ClearGhostPlacementFlags(Player player)
        {
            GameObject ghost = PlacementGhostRef(player);
            if (ghost == null) return;
            Piece p = ghost.GetComponent<Piece>();
            if (p == null) return;
            if (s_flagsActive) RestoreGhostPlacementFlags(player);

            s_savedFlags = new GhostFlags
            {
                GroundPiece = p.m_groundPiece,
                GroundOnly = p.m_groundOnly,
                CultivatedGroundOnly = p.m_cultivatedGroundOnly,
                VegetationGroundOnly = p.m_vegetationGroundOnly,
                NotOnWood = p.m_notOnWood,
                NotOnTiltingSurface = p.m_notOnTiltingSurface,
                NoInWater = p.m_noInWater,
                NoClipping = p.m_noClipping
            };
            s_flagsActive = true;
            s_flagsPiece = p;

            p.m_groundPiece = false;
            p.m_groundOnly = false;
            p.m_cultivatedGroundOnly = false;
            p.m_vegetationGroundOnly = false;
            p.m_notOnWood = false;
            p.m_notOnTiltingSurface = false;
            p.m_noInWater = false;
            p.m_noClipping = false;
        }

        private static void RestoreGhostPlacementFlags(Player player)
        {
            if (!s_flagsActive) return;
            GameObject ghost = PlacementGhostRef(player);
            Piece p = ghost != null ? ghost.GetComponent<Piece>() : null;
            if (p != null && p == s_flagsPiece)
            {
                p.m_groundPiece = s_savedFlags.GroundPiece;
                p.m_groundOnly = s_savedFlags.GroundOnly;
                p.m_cultivatedGroundOnly = s_savedFlags.CultivatedGroundOnly;
                p.m_vegetationGroundOnly = s_savedFlags.VegetationGroundOnly;
                p.m_notOnWood = s_savedFlags.NotOnWood;
                p.m_notOnTiltingSurface = s_savedFlags.NotOnTiltingSurface;
                p.m_noInWater = s_savedFlags.NoInWater;
                p.m_noClipping = s_savedFlags.NoClipping;
            }
            s_flagsActive = false;
            s_flagsPiece = null;
        }

        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        private static void Character_Awake_Postfix(Character __instance)
        {
            if (__instance == null || !IsTroll(__instance)) return;
            if (__instance.GetComponent<TrollPiecesContainer>() == null)
                __instance.gameObject.AddComponent<TrollPiecesContainer>();
        }

        // ================================================================
        //  FIX 17a: ГИБРИДНЫЙ ГЕЙТ. Навигатор и ломание — ТОЛЬКО в режиме
        //  маршрута TrollWalk (ZDO-флаг) и ТОЛЬКО при наличии построек.
        //  Следование/охрана/idle/дикий тролль/без платформы — чистая ваниль.
        // ================================================================
        [HarmonyPatch(typeof(BaseAI), "MoveTo")]
        [HarmonyPrefix]
        private static bool BaseAI_MoveTo_Prefix(
            BaseAI __instance, float dt, Vector3 point, float dist, bool run, ref bool __result)
        {
            try
            {
                if (__instance == null) return true;
                if (!__instance.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase)) return true;
                TrollPiecesContainer container = __instance.GetComponent<TrollPiecesContainer>();
                if (container == null) return true;

                if (container.PieceCount == 0) return true; // без платформы — ваниль

                if (!IsRouteActive(container))
                {
                    container.Navigator.OnRouteEnded(); // маршрут кончился — сброс ломания
                    return true; // следование/охрана — чистая ваниль
                }

                __result = container.Navigator.DriveMoveTo(__instance, dt, point, dist, run);
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] Hybrid MoveTo failed, falling back to vanilla: " + e.Message);
                return true;
            }
        }

        private static bool IsRouteActive(TrollPiecesContainer container)
        {
            try
            {
                ZNetView nv = container.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) return false;
                ZDO zdo = nv.GetZDO();
                return zdo != null && zdo.GetBool(TrollWalkConstants.HashActive, false);
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(Player), "PieceRayTest")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        [HarmonyAfter("com.custom.shipbuildmod")]
        private static bool PieceRayTest_Prefix(
            Player __instance,
            ref bool __result,
            out Vector3 point, out Vector3 normal, out Piece piece,
            out Heightmap heightmap, out Collider waterSurface,
            bool water)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            piece = null;
            heightmap = null;
            waterSurface = null;
            __result = false;

            TrollBuildContext.Reset();

            if (TrollBuildConstants.IsDedicatedServer || GameCamera.instance == null)
                return false;

            if (!s_loggedPatchActive)
            {
                s_loggedPatchActive = true;
                Debug.Log("[TrollBuild] PieceRayTest replacement active");
            }

            int layerMask = water ? PlaceWaterRayMaskRef(__instance) : PlaceRayMaskRef(__instance);
            if (layerMask == 0)
                layerMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");

            GameObject ghost = PlacementGhostRef(__instance);
            Piece ghostPiece = ghost != null ? ghost.GetComponent<Piece>() : null;

            float vanillaMaxDist = __instance.m_maxPlaceDistance;
            if (ghostPiece != null) vanillaMaxDist += ghostPiece.m_extraPlacementDistance;
            float trollMaxDist = vanillaMaxDist + TrollBuildConstants.TrollExtraReach;

            Vector3 eyePos = __instance.m_eye != null ? __instance.m_eye.position : GameCamera.instance.transform.position;
            int combined = layerMask | LayerMask.GetMask("character", "character_net");
            int characterMask = LayerMask.GetMask("character", "character_net");
            int waterLayer = LayerMask.NameToLayer("Water");

            RaycastHit[] hits = Physics.RaycastAll(
                GameCamera.instance.transform.position,
                GameCamera.instance.transform.forward,
                50f, combined);
            if (hits.Length > 1) Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                Collider col = hit.collider;
                if (col == null) continue;

                TrollPieceTag tag = col.GetComponentInParent<TrollPieceTag>();
                if (tag != null && tag.Container != null)
                {
                    if (Vector3.Distance(eyePos, hit.point) > trollMaxDist) continue;

                    Piece hitPiece = col.GetComponentInParent<Piece>();
                    TrollBuildContext.SetTargetPiece(tag.Container, hitPiece, hit.point, hit.normal);
                    ClearGhostPlacementFlags(__instance);
                    LogTargetChange("troll-piece");

                    point = hit.point;
                    normal = hit.normal;
                    piece = hitPiece;
                    heightmap = null;
                    waterSurface = null;
                    __result = true;
                    return false;
                }

                bool isCharacterLayer = (characterMask & (1 << col.gameObject.layer)) != 0;

                if (isCharacterLayer)
                {
                    if (col.GetComponentInParent<Player>() != null) continue;

                    if (Vector3.Distance(eyePos, hit.point) <= trollMaxDist)
                    {
                        Character ch = col.GetComponentInParent<Character>();
                        if (ch != null && IsTroll(ch) && ch.IsTamed())
                        {
                            TrollPiecesContainer container = ch.GetComponent<TrollPiecesContainer>();
                            if (container == null) container = ch.gameObject.AddComponent<TrollPiecesContainer>();

                            if (container.IsInBuildZone(hit.point, TrollBuildConstants.ZoneAcceptanceMult))
                            {
                                container.InitUUID();
                                container.EnsureBuildRoot();
                                Transform root = container.BuildRoot;

                                Vector3 lp = root.InverseTransformPoint(hit.point);
                                Vector3 he = TrollBuildConstants.BuildZoneHalfExtents;
                                lp.x = Mathf.Clamp(lp.x, -he.x, he.x);
                                lp.y = Mathf.Clamp(lp.y, -he.y, he.y);
                                lp.z = Mathf.Clamp(lp.z, -he.z, he.z);
                                Vector3 aim = root.TransformPoint(lp);

                                TrollBuildContext.SetTargetSkin(container, aim, root.up);
                                ClearGhostPlacementFlags(__instance);
                                LogTargetChange("troll-skin");

                                point = aim;
                                normal = root.up;
                                piece = null;
                                heightmap = null;
                                waterSurface = null;
                                __result = true;
                                return false;
                            }
                            LogZoneReject(ch, hit.point);
                        }
                    }
                    continue;
                }

                if (Vector3.Distance(eyePos, hit.point) > vanillaMaxDist) return false;

                Ship ship = col.GetComponentInParent<Ship>();
                if (col.attachedRigidbody != null && ship == null) return false;

                point = hit.point;
                normal = ship != null ? ship.transform.up : hit.normal;
                piece = col.GetComponentInParent<Piece>();
                heightmap = col.GetComponent<Heightmap>();
                waterSurface = (col.gameObject.layer == waterLayer) ? col : null;
                __result = true;
                return false;
            }

            return false;
        }

        private static Vector3 s_lockedLocalPos = Vector3.zero;
        private static TrollPiecesContainer s_lockContainer;
        private static string s_lockGhostName = "";
        private static int s_lastPlaceRot = -1;
        private static int s_lastSnapIndex = -2;
        private static bool s_hasLockedPosition;

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void UpdatePlacementGhost_Postfix(Player __instance)
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            RestoreGhostPlacementFlags(__instance);

            GameObject ghost = PlacementGhostRef(__instance);

            if (!TrollBuildContext.IsActive || ghost == null)
            {
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            var container = TrollBuildContext.Container;
            Transform root = container.BuildRoot;
            if (root == null)
            {
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            if (!ghost.activeInHierarchy) ghost.SetActive(true);

            Piece ghostPiece = ghost.GetComponent<Piece>();
            int placeRotation = PlaceRotationRef(__instance);
            float rotDeg = PlaceRotationDegreesRef(__instance);
            int snapIndex = ManualSnapPointRef(__instance);

            Quaternion localYaw = Quaternion.Euler(0f, rotDeg * placeRotation, 0f);
            Quaternion ghostRot = root.rotation * localYaw;
            ghost.transform.rotation = ghostRot;

            Vector3 aim = TrollBuildContext.AimPoint;
            Vector3 lift = TrollBuildContext.AimNormal;
            try
            {
                if (ghostPiece != null && ghostPiece.m_clipEverything)
                {
                    s_ghostSnapPoints.Clear();
                    ghostPiece.GetSnapPoints(s_ghostSnapPoints);
                    if (snapIndex >= 0 && snapIndex < s_ghostSnapPoints.Count)
                        ghost.transform.position = aim + ghostRot * (-s_ghostSnapPoints[snapIndex].localPosition);
                    else
                        ghost.transform.position = aim;
                }
                else
                {
                    ghost.transform.position = aim + lift * 50f;
                    Vector3 closest = Vector3.zero;
                    float closestDist = float.MaxValue;
                    bool found = false;
                    foreach (Collider c in ghost.GetComponentsInChildren<Collider>())
                    {
                        if (c == null || c.isTrigger || !c.enabled) continue;
                        MeshCollider mc = c as MeshCollider;
                        if (mc != null && !mc.convex) continue;
                        Vector3 p = c.ClosestPoint(aim);
                        float d = Vector3.Distance(p, aim);
                        if (d < closestDist) { closestDist = d; closest = p; found = true; }
                    }

                    if (found)
                    {
                        Vector3 offset = ghost.transform.position - closest;

                        if (snapIndex >= 0)
                        {
                            s_ghostSnapPoints.Clear();
                            if (ghostPiece != null) ghostPiece.GetSnapPoints(s_ghostSnapPoints);
                            if (snapIndex < s_ghostSnapPoints.Count)
                                ghost.transform.position = aim + ghostRot * (-s_ghostSnapPoints[snapIndex].localPosition);
                            else
                                ghost.transform.position = aim + offset;
                        }
                        else
                        {
                            ghost.transform.position = aim + offset;
                        }

                        bool alt = (ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive())
                            ? AltPlaceRef(__instance)
                            : (ZInput.GetButton("AltPlace") || (ZInput.GetButton("JoyAltPlace") && !ZInput.GetButton("JoyRotate")));

                        if (!alt && s_findSnapMethod != null)
                        {
                            List<Piece> tempPieces = TempPiecesRef(__instance);
                            if (tempPieces == null) { tempPieces = new List<Piece>(); TempPiecesRef(__instance) = tempPieces; }
                            tempPieces.Clear();

                            object[] args = { ghost.transform, 0.5f, null, null, tempPieces };
                            if ((bool)s_findSnapMethod.Invoke(__instance, args))
                            {
                                Transform ghostSnap = (Transform)args[2];
                                Transform targetSnap = (Transform)args[3];
                                if (ghostSnap != null && targetSnap != null)
                                {
                                    Vector3 newPos = targetSnap.position - (ghostSnap.position - ghost.transform.position);
                                    bool allowRotated = ghostPiece != null && ghostPiece.m_allowRotatedOverlap;
                                    bool overlap = false;
                                    if (s_overlapMethod != null)
                                    {
                                        object[] args2 = { newPos, ghost.transform.rotation, ghost.name, tempPieces, allowRotated };
                                        overlap = (bool)s_overlapMethod.Invoke(__instance, args2);
                                    }
                                    if (!overlap) ghost.transform.position = newPos;
                                }
                            }
                        }
                    }
                    else
                    {
                        ghost.transform.position = aim;
                    }
                }
            }
            catch { }

            Vector2 mouseDelta = ZInput.GetMouseDelta();
            bool aimInput = mouseDelta.sqrMagnitude > 0.0001f
                || Mathf.Abs(ZInput.GetJoyRightStickX()) > 0.05f
                || Mathf.Abs(ZInput.GetJoyRightStickY()) > 0.05f
                || MoveDirRef(__instance).sqrMagnitude > 0.001f;

            bool changed = aimInput
                || placeRotation != s_lastPlaceRot
                || snapIndex != s_lastSnapIndex
                || s_lockContainer != container
                || s_lockGhostName != ghost.name
                || !s_hasLockedPosition;

            if (changed)
            {
                s_lockedLocalPos = root.InverseTransformPoint(ghost.transform.position);
                s_lastPlaceRot = placeRotation;
                s_lastSnapIndex = snapIndex;
                s_lockContainer = container;
                s_lockGhostName = ghost.name;
                s_hasLockedPosition = true;
            }

            ghost.transform.position = root.TransformPoint(s_lockedLocalPos);
            ghost.transform.rotation = ghostRot;

            if (TrollBuildContext.TargetType == TrollBuildTargetType.TrollPiece && TrollBuildContext.TargetPiece != null)
            {
                WearNTear targetWnt = TrollBuildContext.TargetPiece.GetComponent<WearNTear>();
                if (targetWnt != null && !targetWnt.m_supports)
                {
                    PlacementStatusRef(__instance) = Player.PlacementStatus.Invalid;
                    if (ghostPiece != null) ghostPiece.SetInvalidPlacementHeightlight(true);
                    return;
                }
            }

            var status = PlacementStatusRef(__instance);
            if (status != Player.PlacementStatus.PrivateZone
                && status != Player.PlacementStatus.NoBuildZone
                && status != Player.PlacementStatus.Invalid
                && status != Player.PlacementStatus.BlockedbyPlayer)
            {
                PlacementStatusRef(__instance) = Player.PlacementStatus.Valid;
                if (ghostPiece != null) ghostPiece.SetInvalidPlacementHeightlight(false);
            }
        }

        [HarmonyPatch(typeof(Player), "TestGhostClipping")]
        [HarmonyPrefix]
        private static bool TestGhostClipping_Prefix(ref bool __result)
        {
            if (TrollBuildContext.IsActive)
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Piece), "GetSnapPoints",
            new[] { typeof(Vector3), typeof(float), typeof(List<Transform>), typeof(List<Piece>) })]
        [HarmonyPostfix]
        private static void Piece_GetSnapPoints_Postfix(List<Transform> points)
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            if (!TrollBuildContext.IsActive) return;
            var container = TrollBuildContext.Container;
            if (container == null || container.DeckSnapPoints.Count == 0) return;
            points.AddRange(container.DeckSnapPoints);
        }

        [HarmonyPatch(typeof(Player), "FindHoverObject")]
        [HarmonyPostfix]
        private static void FindHoverObject_Postfix(Player __instance, ref GameObject hover, ref Character hoverCreature)
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            if (hoverCreature == null || !IsTroll(hoverCreature)) return;

            RaycastHit[] hits = RaycastHoverHitsRef != null ? RaycastHoverHitsRef(__instance) : null;
            if (hits == null) return;

            foreach (RaycastHit hit in hits)
            {
                Collider col = hit.collider;
                if (col == null) break;

                TrollPieceTag tag = col.GetComponentInParent<TrollPieceTag>();
                if (tag == null) continue;

                GameObject resolved = ResolvePieceTarget(col, tag);
                hoverCreature = null;
                if (resolved != null)
                {
                    hover = resolved;
                }
                else
                {
                    hover = null;
                }
                return;
            }
        }

        private static GameObject ResolvePieceTarget(Collider col, TrollPieceTag tag)
        {
            Transform root = tag.transform;
            Transform t = col.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                Interactable inter = t.GetComponent<Interactable>();
                if (inter != null) return (inter as MonoBehaviour).gameObject;

                Hoverable hov = t.GetComponent<Hoverable>();
                if (hov != null && !(hov is Character)) return (hov as MonoBehaviour).gameObject;

                if (t == root) break;
                t = t.parent;
            }
            return null;
        }

        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPostfix]
        private static void PlacePiece_Postfix(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (!TrollBuildContext.IsActive || TrollBuildContext.Container == null)
            {
                TrollBuildContext.Reset();
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            var container = TrollBuildContext.Container;
            Transform root = container.BuildRoot;
            if (root == null) { TrollBuildContext.Reset(); return; }

            container.InitUUID();
            if (string.IsNullOrEmpty(container.TrollUUID)) { TrollBuildContext.Reset(); return; }

            Piece placed = null;
            var placedList = AccessTools.StaticFieldRefAccess<List<IPlaced>>(typeof(Player), "m_placed");
            if (placedList != null)
            {
                foreach (var item in placedList)
                    if (item is Piece p && !Player.IsPlacementGhost(p.gameObject)) { placed = p; break; }
            }
            if (placed == null)
            {
                Collider[] cols = Physics.OverlapSphere(pos, 2.5f);
                float min = float.MaxValue;
                foreach (var c in cols)
                {
                    Piece p = c.GetComponentInParent<Piece>();
                    if (p == null || Player.IsPlacementGhost(p.gameObject)) continue;
                    if (piece != null && !p.gameObject.name.StartsWith(piece.gameObject.name)) continue;
                    float d = Vector3.Distance(p.transform.position, pos);
                    if (d < min) { min = d; placed = p; }
                }
            }

            if (placed != null)
            {
                ZNetView nview = placed.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    Vector3 localPos = root.InverseTransformPoint(placed.transform.position);
                    Quaternion localRot = Quaternion.Inverse(root.rotation) * placed.transform.rotation;

                    ZDO zdo = nview.GetZDO();
                    zdo.Set(TrollBuildConstants.HashTrollUUID, container.TrollUUID);
                    zdo.Set(TrollBuildConstants.HashLocalPos, localPos);
                    zdo.Set(TrollBuildConstants.HashLocalRotQ, localRot);
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);

                    if (TrollBuildContext.TargetType == TrollBuildTargetType.TrollPiece
                        && TrollBuildContext.TargetPiece != null)
                    {
                        ZNetView parentNv = TrollBuildContext.TargetPiece.GetComponent<ZNetView>();
                        if (parentNv != null && parentNv.GetZDO() != null)
                            zdo.Set(TrollBuildConstants.ParentPieceHashPair, parentNv.GetZDO().m_uid);
                    }

                    container.RegisterPiece(nview, localPos, localRot);
                }
            }

            TrollBuildContext.Reset();
            s_hasLockedPosition = false;
            s_lockContainer = null;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        private static bool WearNTear_UpdateSupport_Prefix(WearNTear __instance)
        {
            if (__instance == null) return true;
            TrollPieceTag tag = __instance.GetComponent<TrollPieceTag>();
            if (tag != null && tag.Container != null)
            {
                TrollBuildSupport.CalculateTrollPieceSupport(__instance);
                return false;
            }

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.GetZDO() != null &&
                !string.IsNullOrEmpty(nv.GetZDO().GetString(TrollBuildConstants.HashTrollUUID, "")))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateWear")]
        [HarmonyPrefix]
        private static bool WearNTear_UpdateWear_Prefix(WearNTear __instance)
        {
            if (__instance == null) return true;

            TrollPieceTag tag = __instance.GetComponent<TrollPieceTag>();
            if (tag != null && tag.Container != null)
            {
                return Time.time - tag.AttachedAt >= 5f;
            }

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.GetZDO() != null &&
                !string.IsNullOrEmpty(nv.GetZDO().GetString(TrollBuildConstants.HashTrollUUID, "")))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        [HarmonyPostfix]
        private static void WearNTear_OnDestroy_Postfix(WearNTear __instance)
        {
            if (__instance == null) return;
            TrollPieceTag tag = __instance.GetComponent<TrollPieceTag>();
            if (tag != null && tag.Container != null)
                tag.Container.OnPieceDestroyed(__instance);
        }

        [HarmonyPatch(typeof(ZSyncTransform), "ClientSync")]
        [HarmonyPrefix]
        private static bool ZSyncTransform_ClientSync_Prefix(ZSyncTransform __instance)
        {
            if (__instance != null && __instance.GetComponent<TrollPieceTag>() != null) return false;
            return true;
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        [HarmonyPostfix]
        private static void ZNetView_Awake_Postfix(ZNetView __instance)
        {
            if (__instance == null) return;
            ZDO zdo = __instance.GetZDO();
            if (zdo == null) return;
            if (__instance.GetComponent<TrollPieceTag>() != null) return;

            if (__instance.GetComponent<Character>() != null) return;
            if (__instance.GetComponent<TrollPiecesContainer>() != null) return;
            if (__instance.GetComponent<Piece>() == null) return;

            string trollUUID = zdo.GetString(TrollBuildConstants.HashTrollUUID, "");
            if (string.IsNullOrEmpty(trollUUID)) return;

            TrollPieceZdoIndex.Index(zdo);

            if (TrollRegistry.TryGetTroll(trollUUID, out var container))
                TrollPieceAttachmentQueue.Attach(container, __instance);
            else
                TrollPieceAttachmentQueue.Enqueue(trollUUID, __instance);
        }

        [HarmonyPatch(typeof(Character), "ApplyGroundForce")]
        [HarmonyPrefix]
        private static bool ApplyGroundForce_Prefix(Character __instance, ref Vector3 vel, Vector3 targetVel)
        {
            if (__instance == null) return true;
            if (!__instance.IsOnGround()) return true;

            Rigidbody groundBody = LastGroundBodyRef(__instance);
            TrollPiecesContainer container = null;

            if (groundBody != null)
                container = groundBody.GetComponent<TrollPiecesContainer>();
            if (container == null)
            {
                Collider gc = LastGroundColliderRef(__instance);
                if (gc != null) container = gc.GetComponentInParent<TrollPiecesContainer>();
            }
            if (container == null) return true;

            Rigidbody body = BodyRef(__instance);
            Transform root = container.BuildRoot;
            if (body == null || root == null) return true;

            Vector3 pv = container.PlatformVelocity;
            pv.y = 0f;
            vel += pv;

            Vector3 av = container.PlatformAngularVelocity;
            if (av.sqrMagnitude > 0f)
            {
                Vector3 r = body.position - root.position;
                Vector3 rotVel = Vector3.Cross(av, r);
                if (rotVel.sqrMagnitude < 400f) vel += rotVel;
            }

            if (targetVel.magnitude > 0.01f)
            {
                LastAttachBodyRef(__instance) = null;
            }
            else
            {
                if (LastAttachBodyRef(__instance) != groundBody)
                {
                    LastAttachBodyRef(__instance) = groundBody;
                    LastAttachPosRef(__instance) = root.InverseTransformPoint(body.position);
                }
                Vector3 target = root.TransformPoint(LastAttachPosRef(__instance));
                Vector3 delta = target - body.position;
                if (delta.magnitude < 4f)
                {
                    delta.y = 0f;
                    vel += delta * 10f;
                    if (delta.magnitude > 1f)
                        body.position = new Vector3(target.x, body.position.y, target.z);
                }
                else
                {
                    LastAttachBodyRef(__instance) = null;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(Character), "UpdateRotation")]
        [HarmonyPrefix]
        private static void Character_UpdateRotation_Prefix(Character __instance, ref float turnSpeed, float dt)
        {
            if (__instance == null || !IsTroll(__instance)) return;
            TrollPiecesContainer container = __instance.GetComponent<TrollPiecesContainer>();
            if (container == null || container.PieceCount == 0) return;
            if (turnSpeed > TrollBuildConstants.MaxTurnSpeedWithPieces)
                turnSpeed = TrollBuildConstants.MaxTurnSpeedWithPieces;
            container.LimitTurnSpeedForPlatform(ref turnSpeed, dt);
        }
    }

    internal static class TrollPieceDamageFilter
    {
        private static float s_lastLog = -10f;

        private static bool IsTroll(Character c)
        {
            return c != null && c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldBlock(WearNTear wnt, HitData hit)
        {
            try
            {
                if (wnt == null || hit == null) return false;

                TrollPieceTag tag = wnt.GetComponent<TrollPieceTag>();
                if (tag == null || tag.Container == null) return false;

                Character attacker = hit.GetAttacker();
                if (!IsTroll(attacker)) return false;

                if (Time.time - s_lastLog > 10f)
                {
                    s_lastLog = Time.time;
                    Debug.Log($"[TrollBuild] Blocked troll damage: '{attacker.name}' -> piece '{wnt.gameObject.name}' mounted on a troll");
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch]
    public static class TrollWearNTearDamageGuard
    {
        [HarmonyPatch(typeof(WearNTear), "Damage")]
        [HarmonyPrefix]
        private static bool Damage_Prefix(WearNTear __instance, HitData hit)
        {
            return !TrollPieceDamageFilter.ShouldBlock(__instance, hit);
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
        [HarmonyPrefix]
        private static bool RPC_Damage_Prefix(WearNTear __instance, HitData hit)
        {
            return !TrollPieceDamageFilter.ShouldBlock(__instance, hit);
        }
    }

    public static class TrollPieceZdoHelper
    {
        private static FieldInfo s_sectorDict;

        public static bool IsPortalPrefab(GameObject prefab)
        {
            return prefab != null && prefab.GetComponent<TeleportWorld>() != null;
        }

        public static bool IsPortalZDO(ZDO zdo)
        {
            try
            {
                if (zdo == null) return false;
                if (Game.instance != null && Game.instance.PortalPrefabHash != null)
                    return Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab());
                if (ZNetScene.instance == null) return false;
                return IsPortalPrefab(ZNetScene.instance.GetPrefab(zdo.GetPrefab()));
            }
            catch { return false; }
        }

        public static void MovePieceZDO(ZDO zdo, Vector3 pos, Quaternion rot)
        {
            if (zdo == null || ZDOMan.instance == null) return;
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer() && !zdo.IsOwner())
                    zdo.SetOwner(ZDOMan.GetSessionID());

                if (!IsPortalZDO(zdo))
                {
                    zdo.SetPosition(pos);
                    zdo.SetRotation(rot);
                    return;
                }

                ZoneSystem.SectorIndex oldSector = zdo.GetSectorIndex();
                ZoneSystem.SectorIndex newSector = ZoneSystem.GetSectorIndex(pos);

                zdo.SetPosition(pos);
                zdo.SetRotation(rot);
                ZDOMan.instance.SetDirtyPortals();

                if (!oldSector.Equals(newSector))
                {
                    try
                    {
                        var portals = ZDOMan.instance.GetPortals();
                        if (portals != null)
                        {
                            if (portals.TryGetValue(oldSector, out List<ZDO> oldList))
                            {
                                oldList.Remove(zdo);
                                if (oldList.Count == 0) portals.Remove(oldSector);
                            }

                            if (!portals.TryGetValue(newSector, out List<ZDO> newList))
                            {
                                newList = new List<ZDO>();
                                portals[newSector] = newList;
                            }
                            if (!newList.Contains(zdo))
                                newList.Add(zdo);
                        }
                    }
                    catch (Exception e) { Debug.LogWarning("[TrollBuild] Portal rekey failed: " + e.Message); }

                    if (ZNet.instance != null && ZNet.instance.IsServer())
                        ZDOMan.instance.ZDOSectorInvalidated(zdo);

                    Debug.Log($"[TrollBuild] Portal ZDO {zdo.m_uid} resector {oldSector.Sector} -> {newSector.Sector} pos={pos.ToString("F1")}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] MovePieceZDO failed: " + e.Message);
            }
        }

        public static void PurgeZombieZDO(ZDO zdo)
        {
            try
            {
                if (s_sectorDict == null)
                    s_sectorDict = FindSectorDict();
                if (s_sectorDict == null || ZDOMan.instance == null)
                {
                    Debug.LogWarning("[TrollBuild] ZDOMan sector dictionary not found");
                    return;
                }
                object dict = s_sectorDict.GetValue(ZDOMan.instance);
                if (dict == null) return;
                PropertyInfo valuesProp = dict.GetType().GetProperty("Values");
                object values = valuesProp != null ? valuesProp.GetValue(dict, null) : null;
                if (values == null) return;

                int removed = 0;
                foreach (object listObj in (System.Collections.IEnumerable)values)
                {
                    if (listObj is ICollection<ZDO> col)
                    {
                        while (col.Remove(zdo)) removed++;
                    }
                }
                if (removed > 0)
                    Debug.Log($"[TrollBuild] Purged zombie ZDO from sector lists (entries: {removed})");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] PurgeZombieZDO failed: " + e.Message);
            }
        }

        private static FieldInfo FindSectorDict()
        {
            foreach (FieldInfo f in typeof(ZDOMan).GetFields(AccessTools.all))
            {
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length == 2 && typeof(IEnumerable<ZDO>).IsAssignableFrom(args[1]))
                    return f;
            }
            return null;
        }
    }

    [HarmonyPatch]
    public static class TrollZombieZdoCleanup
    {
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        [HarmonyPrefix]
        private static bool CreateObject_Prefix(ZDO zdo, ref GameObject __result)
        {
            if (zdo == null || (zdo.m_uid != ZDOID.None && zdo.IsValid()))
                return true;

            if (zdo != null)
            {
                TrollPieceZdoHelper.PurgeZombieZDO(zdo);
                Debug.LogWarning("[TrollBuild] Cleaned up zombie ZDO (uid=0:0) left by a broken portal");
            }
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    public static class TrollStuckZdoCleanup
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instancesField =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPostfix]
        private static void RemoveObjects_Postfix(ZNetScene __instance)
        {
            try
            {
                var instances = s_instancesField(__instance);
                if (instances == null || instances.Count == 0) return;

                List<ZDO> deadKeys = null;
                foreach (var kvp in instances)
                {
                    if (kvp.Value != null) continue;
                    (deadKeys ??= new List<ZDO>()).Add(kvp.Key);
                }
                if (deadKeys == null) return;

                foreach (ZDO zdo in deadKeys)
                {
                    instances.Remove(zdo);
                    if (zdo != null && zdo.m_uid != ZDOID.None && zdo.IsValid())
                    {
                        zdo.Created = false;
                        Debug.Log($"[TrollBuild] Revived stuck ZDO {zdo.m_uid} (dead GO, Created reset)");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] RemoveObjects cleanup failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch]
    public static class TrollPortalTeleport_Patch
    {
        private static readonly MethodInfo s_pokeLocalZone =
            AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone");

        private static void PokeZone(Vector2s zone)
        {
            try
            {
                if (ZoneSystem.instance != null && s_pokeLocalZone != null)
                    s_pokeLocalZone.Invoke(ZoneSystem.instance, new object[] { zone });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] PokeLocalZone failed: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(TeleportWorld), "Teleport")]
        [HarmonyPrefix]
        private static bool Teleport_Prefix(TeleportWorld __instance, Player player)
        {
            try
            {
                if (player == null) return true;
                ZNetView nv = __instance.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || nv.GetZDO() == null) return true;

                ZDOID targetId = nv.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (targetId == ZDOID.None) return true;
                ZDO targetZdo = ZDOMan.instance.GetZDO(targetId);
                if (targetZdo == null) return true;

                ZNetView anyView = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(targetZdo) : null;
                bool goVisible = anyView != null && anyView.gameObject.activeInHierarchy;

                Vector3 pos = goVisible ? anyView.transform.position : targetZdo.GetPosition();
                Quaternion rot = goVisible ? anyView.transform.rotation : targetZdo.GetRotation();

                Vector3 exit = pos + rot * Vector3.forward * __instance.m_exitDistance + Vector3.up;

                if (!goVisible)
                {
                    ClampToGround(ref exit, 1.5f);

                    ZDOID trollId = ParseZDOID(targetZdo.GetString(TrollBuildConstants.HashTrollUUID, ""));
                    if (trollId != ZDOID.None)
                    {
                        ZDO trollZdo = ZDOMan.instance.GetZDO(trollId);
                        if (trollZdo != null)
                        {
                            Vector3 away = exit - trollZdo.GetPosition();
                            away.y = 0f;
                            if (away.sqrMagnitude < 0.01f)
                                away = rot * Vector3.forward;
                            exit += away.normalized * 2.5f;
                            ClampToGround(ref exit, 1.5f);
                        }
                    }
                }

                Debug.Log($"[TrollBuild] Portal teleport start: targetGO={goVisible} exit={exit.ToString("F1")}");
                player.TeleportTo(exit, rot, true);

                if (TrollWalkManager.Instance != null)
                    TrollWalkManager.Instance.StartCoroutine(
                        DeliverPlayerToPortal(player, targetId, __instance.m_exitDistance));

                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrollBuild] Portal teleport patch failed: " + e.Message);
                return true;
            }
        }

        private static void ClampToGround(ref Vector3 exit, float threshold)
        {
            try
            {
                float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(exit) : 0f;
                if (ground > 0f && exit.y > ground + threshold) exit.y = ground + 1f;
            }
            catch { }
        }

        private static IEnumerator DeliverPlayerToPortal(Player player, ZDOID targetId, float exitDistance)
        {
            float deadline = Time.realtimeSinceStartup + 25f;

            while (player != null && player && player.IsTeleporting() &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            if (player == null || !player) yield break;

            ZNetView targetView = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (ZNetScene.instance == null) yield break;
                ZDO tz = ZDOMan.instance.GetZDO(targetId);
                if (tz != null)
                {
                    ZNetView anyView = ZNetScene.instance.FindInstance(tz);
                    if (anyView != null && anyView.gameObject.activeInHierarchy)
                    {
                        targetView = anyView;
                        break;
                    }

                    Rigidbody rb = player.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                    }

                    PokeZone(ZoneSystem.GetZone(tz.GetPosition()));
                }
                yield return new WaitForFixedUpdate();
            }

            if (targetView == null)
            {
                Debug.Log("[TrollBuild] Portal delivery: target GO did not spawn in time");
                yield break;
            }

            yield return new WaitForSeconds(0.4f);

            ZDO tz2 = ZDOMan.instance.GetZDO(targetId);
            ZNetView finalView = tz2 != null && ZNetScene.instance != null
                ? ZNetScene.instance.FindInstance(tz2) : null;
            if (finalView == null || !finalView.gameObject.activeInHierarchy)
                yield break;

            Vector3 pos = finalView.transform.position;
            Quaternion rot = finalView.transform.rotation;
            Vector3 exit = pos + rot * Vector3.forward * exitDistance + Vector3.up;
            ClampToGround(ref exit, 6f);

            if (player != null && player)
            {
                player.transform.position = exit;
                Rigidbody rb2 = player.GetComponent<Rigidbody>();
                if (rb2 != null)
                {
                    rb2.position = exit;
                    rb2.linearVelocity = Vector3.zero;
                    rb2.angularVelocity = Vector3.zero;
                }
                if (ZNet.instance != null) ZNet.instance.SetReferencePosition(exit);
                Debug.Log($"[TrollBuild] Player delivered to portal GO at {exit.ToString("F1")}");
            }
        }

        private static ZDOID ParseZDOID(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return ZDOID.None;
                string[] parts = s.Split(':');
                if (parts.Length == 2 &&
                    long.TryParse(parts[0], out long user) &&
                    uint.TryParse(parts[1], out uint id))
                    return new ZDOID(user, id);
            }
            catch { }
            return ZDOID.None;
        }
    }
}