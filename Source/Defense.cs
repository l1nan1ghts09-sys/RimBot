using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Оборона базы: оценка сложности, вероятности устоять против разных угроз,
    /// выбор тактики, расход материалов и приоритетное строительство укреплений и крыш.
    /// </summary>
    public static class Defense
    {
        public class Report
        {
            public float difficulty;
            public string level = "неизвестно";
            public float risk;
            public float vsMelee, vsRanged, vsSappers, vsDrop, vsInsects, vsManhunters, vsMechs;
            public int turrets, traps, cover, walls, roofed, gunners, fighters, medicine;
            public string tactic = "";
            public string text = "";
            public string shortText = "";
        }

        private static int nextPrep;
        private static Report last;
        private static string lastLevel = "";
        private static int lastReportTick = -99999;

        public static Report Last { get { return last; } }
        public static bool WantsBuilders { get { return last != null && last.difficulty >= 0.45f; } }
        public static string StatusShort { get { return last != null && last.shortText.Length > 0 ? last.shortText : "Оборона: оценка ещё не готова."; } }
        public static string StatusDetail { get { return last != null ? last.text : "Оборона: оценка ещё не готова."; } }

        public static void ResetRuntime()
        {
            nextPrep = 0;
            last = null;
            lastLevel = "";
            lastReportTick = -99999;
        }

        /// <summary>Периодический проход: оценка обстановки и, если нет боя, стройка обороны.</summary>
        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            if (tick < nextPrep) return;
            nextPrep = tick + 1800;
            try
            {
                if (GenHostility.AnyHostileActiveThreatToPlayer(map))
                {
                    last = Assess(map);
                    return;
                }
                Prepare(map, tick);
            }
            catch (Exception e) { T.Warn("Оборона: " + e.Message); }
        }

        /// <summary>Публичный вход для навыка «Подготовка обороны».</summary>
        public static void Prepare(Map map)
        {
            Prepare(map, Find.TickManager != null ? Find.TickManager.TicksGame : 0);
        }

        private static void Prepare(Map map, int tick)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (map == null || s == null || !s.autoDefense) return;

            Report r = Assess(map);
            last = r;

            if (r.level != lastLevel || tick - lastReportTick > 24000)
            {
                lastLevel = r.level;
                lastReportTick = tick;
                Mind.Think("оборона", r.text + " Тактика: " + r.tactic + ".");
            }

            if (!HasBuilder(map)) return;

            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            int placed = 0;
            int pending = PendingBlueprints(map);
            if (pending >= 8) return;

            int coverTarget = Math.Min(8, Math.Max(3, pop));
            int trapTarget = Math.Min(Math.Max(0, s.defenseTrapCap), Math.Max(2, pop / 2 + 1));
            int wallTarget = Math.Min(6, Math.Max(2, pop / 2));

            // Самое дешёвое и быстрое — укрытия на огневой линии; при высокой
            // сложности сначала закрываем самое слабое звено (турели/ловушки).
            if (r.difficulty >= 0.55f && r.turrets < TurretCap(map, pop, r))
                placed += TryTurret(map, r, pop);

            if (r.cover < coverTarget)
                placed += TryFiringLine(map, coverTarget - r.cover, pop);

            if (r.traps < trapTarget)
                placed += TryTraps(map, trapTarget - r.traps, pop);

            if (r.walls < wallTarget && r.difficulty >= 0.40f)
                placed += TryWalls(map, wallTarget - r.walls, pop);

            if (s.defenseRoofs)
                placed += RoofKeyRooms(map);

            if (placed > 0)
                Mind.Think("оборона", "Стройка обороны: укрытий " + r.cover + "/" + coverTarget
                    + ", ловушек " + r.traps + "/" + trapTarget + ", стен " + r.walls + "/" + wallTarget
                    + ", турелей " + r.turrets + "/" + TurretCap(map, pop, r)
                    + ". Материалы: " + MaterialsLine(map) + ".");
        }

        // ------------------------------------------------------------------ оценка

        public static Report AssessNow(Map map) { return Assess(map); }

        private static Report Assess(Map map)
        {
            Report r = new Report();
            if (map == null) return r;

            IntVec3 anchor = Infrastructure.Anchor(map);
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            r.fighters = 0;
            r.gunners = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p == null || p.Dead || p.Downed || p.InMentalState) continue;
                if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                if (p.health != null && p.health.summaryHealth != null && p.health.summaryHealth.SummaryHealthPercent < 0.35f) continue;
                r.fighters++;
                if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.IsRangedWeapon) r.gunners++;
            }

            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b == null || b.def == null) continue;
                if (b is Building_Turret) r.turrets++;
                else if (b is Building_Trap) r.traps++;
                string dn = b.def.defName ?? "";
                if (dn.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("Barricade", StringComparison.OrdinalIgnoreCase) >= 0) r.cover++;
                if (b.def == ThingDefOf.Wall) r.walls++;
            }

            r.medicine = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal)
                + T.Count(map, ThingDefOf.MedicineUltratech);

            foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 20f, true))
                if (c.InBounds(map) && c.Roofed(map)) r.roofed++;

            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;
            r.risk = w != null ? w.baseRisk : 0.25f;
            float wealth = w != null ? w.wealth : 0f;
            if (wealth <= 0f) { try { wealth = map.wealthWatcher.WealthTotal; } catch { } }

            float need = Mathf.Max(4f, pop * 1.8f + wealth / 6000f);
            float power = r.fighters * 1.6f + r.gunners * 1.4f + r.turrets * 4.2f + r.traps * 1.1f
                + r.cover * 0.8f + r.walls * 0.25f + r.medicine * 0.15f;
            float prep = U.Clamp01(power / (power + need));
            float baseP = U.Clamp(0.18f + prep * 0.58f - r.risk * 0.18f, 0.05f, 0.95f);

            r.vsMelee = U.Clamp(baseP + r.traps * 0.025f + r.walls * 0.010f + r.cover * 0.010f, 0.05f, 0.95f);
            r.vsRanged = U.Clamp(baseP + r.cover * 0.018f + r.turrets * 0.020f, 0.05f, 0.95f);
            r.vsSappers = U.Clamp(baseP - 0.10f + r.walls * 0.012f + r.traps * 0.020f + r.gunners * 0.012f, 0.05f, 0.95f);
            r.vsDrop = U.Clamp(baseP - 0.20f + r.gunners * 0.022f + r.fighters * 0.010f, 0.05f, 0.95f);
            r.vsInsects = U.Clamp(baseP - 0.15f + r.fighters * 0.020f + r.medicine * 0.006f, 0.05f, 0.95f);
            r.vsManhunters = U.Clamp(baseP + r.gunners * 0.020f + r.cover * 0.012f, 0.05f, 0.95f);
            r.vsMechs = U.Clamp(baseP - 0.10f + r.turrets * 0.030f + r.gunners * 0.012f, 0.05f, 0.95f);

            float avg = (r.vsMelee + r.vsRanged + r.vsSappers + r.vsDrop + r.vsInsects + r.vsManhunters + r.vsMechs) / 7f;
            r.difficulty = U.Clamp01(1f - avg * 0.85f + r.risk * 0.20f);
            r.level = r.difficulty < 0.35f ? "низкая" : r.difficulty < 0.55f ? "средняя"
                : r.difficulty < 0.75f ? "высокая" : "критическая";
            r.tactic = Tactic(map, r);
            r.text = "Оборона: сложность " + r.level + " (" + U.Pct(r.difficulty) + "), риск рейда " + U.Pct(r.risk)
                + ". Шансы устоять: ближний бой " + U.Pct(r.vsMelee) + ", стрелки " + U.Pct(r.vsRanged)
                + ", сапёры " + U.Pct(r.vsSappers) + ", десант " + U.Pct(r.vsDrop)
                + ", насекомые " + U.Pct(r.vsInsects) + ", звери " + U.Pct(r.vsManhunters) + ", мехи " + U.Pct(r.vsMechs) + ".";
            r.shortText = "Оборона: " + r.level + " (" + U.Pct(r.difficulty) + "), ближний " + U.Pct(r.vsMelee)
                + ", стрелки " + U.Pct(r.vsRanged) + ", сапёры " + U.Pct(r.vsSappers) + ", десант " + U.Pct(r.vsDrop) + ".";
            return r;
        }

        private static string Tactic(Map map, Report r)
        {
            bool threat = false;
            try { threat = GenHostility.AnyHostileActiveThreatToPlayer(map); } catch { }
            if (threat)
            {
                string live = LiveThreat(map);
                if (live == "мехи") return "турели и огонь с дистанции, не подпускать к базе";
                if (live == "насекомые") return "узкий проход, ближний бой, свет и медицина наготове";
                if (live == "десант") return "внутренняя оборона: коридоры, резерв, не выходить под открытое небо";
                if (live == "сапёры") return "стены и ловушки на пути, резерв у пролома";
                if (live == "стрелки") return "огневая линия за укрытиями, турели держат фланг";
                return "встреча у прохода, ловушки и копейщики впереди";
            }
            if (r.difficulty >= 0.70f) return "срочно: укрытия, ловушки, турели, крыша над жильём";
            if (r.difficulty >= 0.50f) return "линия укрытий и ловушки на подходе, турели по флангам";
            return "готовая линия обороны, дежурство и запас медикаментов";
        }

        private static string LiveThreat(Map map)
        {
            int mech = 0, insect = 0, sapper = 0, ranged = 0, melee = 0;
            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p == null || p.Dead || p.Downed || !p.HostileTo(Faction.OfPlayer)) continue;
                    if (!GenHostility.IsActiveThreatToPlayer(p)) continue;
                    object isMech = R.Get(p.RaceProps, "IsMechanoid");
                    if (isMech is bool && (bool)isMech) { mech++; continue; }
                    if (p.RaceProps != null && p.RaceProps.Insect) { insect++; continue; }
                    string kind = p.kindDef != null && p.kindDef.defName != null ? p.kindDef.defName : "";
                    if (kind.IndexOf("Sapper", StringComparison.OrdinalIgnoreCase) >= 0
                        || kind.IndexOf("Breach", StringComparison.OrdinalIgnoreCase) >= 0) { sapper++; continue; }
                    if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.IsRangedWeapon) ranged++;
                    else melee++;
                }
            }
            catch { }
            if (mech >= Math.Max(1, ranged + melee + sapper)) return "мехи";
            if (insect > 0 && insect >= ranged + melee) return "насекомые";
            if (sapper > 0 && sapper >= ranged) return "сапёры";
            if (ranged > melee) return "стрелки";
            return "ближний бой";
        }

        // ------------------------------------------------------------------ стройка

        private static bool HasBuilder(Map map)
        {
            WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Construction");
            if (wt == null) return false;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (p != null && !p.Dead && !p.Downed && !p.InMentalState && !p.WorkTypeIsDisabled(wt)) return true;
            return false;
        }

        private static int PendingBlueprints(Map map)
        {
            try
            {
                int n = 0;
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                    if (t.Faction == Faction.OfPlayer) n++;
                return n;
            }
            catch { return 0; }
        }

        private static int TurretCap(Map map, int pop, Report r)
        {
            int setting = TradeEmpireMod.S != null ? TradeEmpireMod.S.defenseTurretCap : 6;
            int cap = 2 + pop / 3 + (r != null && r.difficulty >= 0.60f ? 1 : 0);
            return Math.Max(0, Math.Min(setting, cap));
        }

        private static int TryTurret(Map map, Report r, int pop)
        {
            if (r.turrets >= TurretCap(map, pop, r)) return 0;
            TradeSettings s = TradeEmpireMod.S;
            int steelNeed = Math.Max(80, (s != null ? s.minSteel : 300) + 40);
            int compNeed = Math.Max(4, (s != null ? s.minComponents : 20) + 2);
            if (T.Count(map, ThingDefOf.Steel) < steelNeed || T.Count(map, ThingDefOf.ComponentIndustrial) < compNeed) return 0;
            ThingDef def = T.Def("Turret_MiniTurret");
            if (def == null || !Infrastructure.Available(def) || Infrastructure.Exists(map, def)) return 0;
            if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 4, 18, false))
            {
                Mind.Think("оборона", "Заложил мини-турель: приоритет обороны, огневая точка у базы.");
                return 1;
            }
            return 0;
        }

        private static int TryFiringLine(Map map, int need, int pop)
        {
            if (need <= 0) return 0;
            IntVec3 anchor = Infrastructure.Anchor(map);
            IntVec3 dir = Outward(map, anchor);
            IntVec3 side = new IntVec3(dir.z, 0, -dir.x);
            int made = 0;
            for (int d = 4; d <= 7 && made < need; d++)
            {
                for (int l = -3; l <= 3 && made < need; l++)
                {
                    if (l == 0) continue;                                // проход в центре
                    if ((d + l) % 2 == 0) continue;                      // не сплошная стена
                    IntVec3 c = new IntVec3(anchor.x + dir.x * d + side.x * l, 0, anchor.z + dir.z * d + side.z * l);
                    if (!Buildable(map, c)) continue;
                    if (TryBuild(map, T.Def("Barricade"), c, true) || TryBuild(map, T.Def("Sandbags"), c, true))
                        made++;
                }
            }
            return made;
        }

        private static int TryTraps(Map map, int need, int pop)
        {
            if (need <= 0) return 0;
            ThingDef trap = T.Def("TrapSpike") ?? T.Def("TrapIed");
            if (trap == null) return 0;
            IntVec3 anchor = Infrastructure.Anchor(map);
            IntVec3 dir = Outward(map, anchor);
            IntVec3 side = new IntVec3(dir.z, 0, -dir.x);
            int made = 0;
            for (int d = 9; d <= 20 && made < need; d += 2)
            {
                for (int l = -3; l <= 3 && made < need; l += 2)
                {
                    IntVec3 c = new IntVec3(anchor.x + dir.x * d + side.x * l, 0, anchor.z + dir.z * d + side.z * l);
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                    if (map.areaManager.Home != null && map.areaManager.Home[c]) continue;   // не на тропах колонии
                    if (c.Roofed(map) || c.GetEdifice(map) != null) continue;
                    if (TryBuild(map, trap, c, false)) made++;
                }
            }
            return made;
        }

        private static int TryWalls(Map map, int need, int pop)
        {
            if (need <= 0) return 0;
            ThingDef wall = ThingDefOf.Wall;
            if (wall == null) return 0;
            IntVec3 anchor = Infrastructure.Anchor(map);
            IntVec3 dir = Outward(map, anchor);
            IntVec3 side = new IntVec3(dir.z, 0, -dir.x);
            int made = 0;
            for (int d = 4; d <= 6 && made < need; d++)
            {
                foreach (int l in new[] { -4, 4 })
                {
                    if (made >= need) break;
                    IntVec3 c = new IntVec3(anchor.x + dir.x * d + side.x * l, 0, anchor.z + dir.z * d + side.z * l);
                    if (!Buildable(map, c)) continue;
                    if (TryBuild(map, wall, c, true)) made++;
                }
            }
            return made;
        }

        /// <summary>Крыша над ключевыми помещениями: спальни, кухня, исследование, склад.</summary>
        private static int RoofKeyRooms(Map map)
        {
            int changed = 0;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.Position.Roofed(map)) continue;
                    string dn = b.def != null && b.def.defName != null ? b.def.defName : "";
                    bool key = b is Building_Bed
                        || dn.IndexOf("Stove", StringComparison.OrdinalIgnoreCase) >= 0
                        || dn.IndexOf("ResearchBench", StringComparison.OrdinalIgnoreCase) >= 0
                        || dn.IndexOf("TableButcher", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!key) continue;
                    changed += RoofRoom(map, b.Position, 60);
                    if (changed > 0) break;
                }
                if (changed == 0)
                {
                    foreach (Zone_Stockpile z in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
                    {
                        foreach (IntVec3 c in z.cells)
                        {
                            if (c.Roofed(map)) continue;
                            changed += RoofRoom(map, c, 60);
                            if (changed > 0) break;
                        }
                        if (changed > 0) break;
                    }
                }
            }
            catch (Exception e) { T.Warn("Крыша обороны: " + e.Message); }
            if (changed > 0)
                Mind.Think("оборона", "Накрыл крышей ключевое помещение (" + changed + " кл.): меньше порчи, дождя и токсичного fallout.");
            return changed;
        }

        /// <summary>Заливает крышей замкнутое помещение вокруг клетки, если оно небольшое и реально окружено стенами.</summary>
        private static int RoofRoom(Map map, IntVec3 start, int maxCells)
        {
            if (!start.InBounds(map) || start.Roofed(map)) return 0;
            var region = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            queue.Enqueue(start);
            region.Add(start);
            int boundary = 0;
            while (queue.Count > 0 && region.Count < maxCells)
            {
                IntVec3 c = queue.Dequeue();
                foreach (IntVec3 n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)))
                {
                    if (!n.InBounds(map) || region.Contains(n)) continue;
                    if (!n.InHorDistOf(start, 14f)) continue;
                    if (n.GetEdifice(map) != null) { boundary++; continue; }
                    if (!n.Standable(map)) continue;
                    region.Add(n);
                    queue.Enqueue(n);
                    if (region.Count >= maxCells) break;
                }
            }
            if (region.Count < 4 || region.Count > maxCells || boundary < 8) return 0;
            foreach (IntVec3 c in region)
                if (c.x <= 1 || c.z <= 1 || c.x >= map.Size.x - 2 || c.z >= map.Size.z - 2) return 0;

            int changed = 0;
            foreach (IntVec3 c in region)
                if (!c.Roofed(map)) { map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed); changed++; }
            return changed;
        }

        // ------------------------------------------------------------------ материалы

        private static bool TryBuild(Map map, ThingDef def, IntVec3 c, bool stoneFirst)
        {
            if (def == null || !c.InBounds(map) || c.Fogged(map)) return false;
            if (!c.Standable(map) || c.GetEdifice(map) != null) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;
            foreach (Thing t in map.thingGrid.ThingsAt(c))
                if (!t.def.CanOverlapZones) return false;
            int cost = StuffCost(def);
            if (!def.MadeFromStuff)
                return Place(map, def, c, null);
            foreach (ThingDef stuff in StuffChoices(map, def, stoneFirst, cost))
            {
                if (stuff == null) continue;
                if (T.Count(map, stuff) < cost) continue;
                if (Place(map, def, c, stuff)) return true;
            }
            return false;
        }

        private static bool Place(Map map, ThingDef def, IntVec3 c, ThingDef stuff)
        {
            try
            {
                if (!GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map, false, null, null, stuff).Accepted) return false;
                GenConstruct.PlaceBlueprintForBuild(def, c, map, Rot4.North, Faction.OfPlayer, stuff);
                return true;
            }
            catch { return false; }
        }

        private static List<ThingDef> StuffChoices(Map map, ThingDef def, bool stoneFirst, int cost)
        {
            var list = new List<ThingDef>();
            Action<ThingDef> add = d => { if (d != null && !list.Contains(d)) list.Add(d); };
            ThingDef granite = T.Def("BlocksGranite"), limestone = T.Def("BlocksLimestone"),
                slate = T.Def("BlocksSlate"), sandstone = T.Def("BlocksSandstone"), marble = T.Def("BlocksMarble");
            if (def.defName != null && def.defName.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0)
                add(T.Def("Cloth"));
            if (stoneFirst)
            {
                add(granite); add(limestone); add(slate); add(sandstone); add(marble);
            }
            add(ThingDefOf.WoodLog);
            if (!stoneFirst)
            {
                add(granite); add(limestone); add(slate); add(sandstone); add(marble);
            }
            add(GenStuff.DefaultStuffFor(def));
            // Сталь и пластисталь — только если их реально много сверх резерва.
            int reserve = TradeEmpireMod.S != null ? TradeEmpireMod.S.minSteel : 300;
            if (T.Count(map, ThingDefOf.Steel) >= reserve + cost) add(ThingDefOf.Steel);
            if (T.Count(map, ThingDefOf.Plasteel) >= 100 + cost) add(ThingDefOf.Plasteel);
            // Никогда не тратим сталь ниже резерва и пластисталь «под ноль».
            return list.Where(d => d != ThingDefOf.Steel || T.Count(map, d) >= reserve + cost)
                       .Where(d => d != ThingDefOf.Plasteel || T.Count(map, d) >= 100 + cost).ToList();
        }

        private static int StuffCost(ThingDef def)
        {
            object v = R.Get(def, "costStuffCount");
            int cost = 5;
            if (v != null) { try { cost = Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { } }
            return Math.Max(1, cost);
        }

        private static string MaterialsLine(Map map)
        {
            int stone = T.Count(map, T.Def("BlocksGranite")) + T.Count(map, T.Def("BlocksLimestone"))
                + T.Count(map, T.Def("BlocksSlate")) + T.Count(map, T.Def("BlocksSandstone")) + T.Count(map, T.Def("BlocksMarble"));
            int wood = T.Count(map, ThingDefOf.WoodLog);
            int cloth = T.Count(map, T.Def("Cloth"));
            int steel = T.Count(map, ThingDefOf.Steel);
            return "блоки " + stone + ", дерево " + wood + ", ткань " + cloth + ", сталь " + steel
                + " (сталь ниже резерва не трогаю)";
        }

        // ------------------------------------------------------------------ бой

        /// <summary>Клетки за укрытиями, куда ставить стрелков. Разные клетки — чтобы не толпились.</summary>
        public static List<IntVec3> CoverCells(Map map)
        {
            var list = new List<IntVec3>();
            if (map == null) return list;
            try
            {
                IntVec3 anchor = Infrastructure.Anchor(map);
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.def == null || b.def.defName == null) continue;
                    string dn = b.def.defName;
                    bool cover = dn.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0
                        || dn.IndexOf("Barricade", StringComparison.OrdinalIgnoreCase) >= 0
                        || b.def == ThingDefOf.Wall;
                    if (!cover) continue;
                    foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(b.Position, map)))
                    {
                        if (!c.InBounds(map) || !c.Standable(map) || c.Roofed(map)) continue;
                        if (c.GetEdifice(map) != null || list.Contains(c)) continue;
                        if (!c.InHorDistOf(anchor, 30f)) continue;
                        list.Add(c);
                    }
                }
                list.Sort((a, b) => a.DistanceToSquared(anchor).CompareTo(b.DistanceToSquared(anchor)));
            }
            catch (Exception e) { T.Warn("Укрытия: " + e.Message); }
            return list;
        }

        private static IntVec3 Outward(Map map, IntVec3 c)
        {
            int left = c.x, right = map.Size.x - 1 - c.x, down = c.z, up = map.Size.z - 1 - c.z;
            int min = Math.Min(Math.Min(left, right), Math.Min(down, up));
            if (min == left) return new IntVec3(-1, 0, 0);
            if (min == right) return new IntVec3(1, 0, 0);
            if (min == down) return new IntVec3(0, 0, -1);
            return new IntVec3(0, 0, 1);
        }

        private static bool Buildable(Map map, IntVec3 c)
        {
            if (!c.InBounds(map) || c.Fogged(map)) return false;
            if (!c.Standable(map) || c.GetEdifice(map) != null) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;      // не застраиваем грядки и склады
            return true;
        }
    }
}
