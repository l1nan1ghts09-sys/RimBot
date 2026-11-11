using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Автономный режим для чистой ванильной игры: без Harmony, DLC и второго DLL.
    /// Берёт на себя то, что раньше делал основной бот: призыв и оборону, охоту,
    /// приручение, посевы, добычу ресурсов, экипировку и базовые задания на верстаках.
    /// Все действия — обычные ванильные приказы (designation / bill / draft / job),
    /// поэтому они работают с любым набором DLC и не требуют патчей.
    /// </summary>
    public static class Vanilla
    {
        private static readonly HashSet<int> botDrafted = new HashSet<int>();
        private static int nextDefense, nextFarm, nextHunt, nextTame, nextMine, nextEquip, nextBills, nextPrison;
        private static int lastThought;
        private static bool announced;

        /// <summary>Основного бота нет — работаем сами.</summary>
        public static bool Active
        {
            get
            {
                try
                {
                    TradeSettings s = TradeEmpireMod.S;
                    if (s == null || !s.vanillaAuto) return false;
                    return !BotBridge.BotPresent;
                }
                catch { return false; }
            }
        }

        public static void ResetRuntime()
        {
            botDrafted.Clear();
            nextDefense = nextFarm = nextHunt = nextTame = nextMine = nextEquip = nextBills = nextPrison = 0;
            lastThought = 0;
            announced = false;
        }

        public static void Run(Map map, int tick)
        {
            if (map == null || !Active) return;
            if (!announced)
            {
                announced = true;
                Mind.Think("решение", "Основной бот не найден: включаю полностью автономный ванильный режим — оборона, охота, посевы, добыча, экипировка и задания на верстаках.");
            }

            T.Safe("VanillaDefense", () => Defense(map, tick));
            if (tick >= nextBills) { nextBills = tick + 6000; T.Safe("VanillaBills", () => Bills(map)); }
            if (tick >= nextEquip) { nextEquip = tick + 3000; T.Safe("VanillaEquip", () => Equip(map)); }
            if (tick >= nextFarm) { nextFarm = tick + 4000; T.Safe("VanillaFarm", () => Farm(map)); }
            if (tick >= nextHunt) { nextHunt = tick + 2500; T.Safe("VanillaHunt", () => Hunt(map)); }
            if (tick >= nextMine) { nextMine = tick + 4000; T.Safe("VanillaMine", () => Mine(map)); }
            if (tick >= nextTame) { nextTame = tick + 6000; T.Safe("VanillaTame", () => Tame(map)); }
            if (tick >= nextPrison) { nextPrison = tick + 6000; T.Safe("VanillaPrison", () => Prisoners(map)); }
        }

        // ============================== оборона ==============================

        private static List<Pawn> Hostiles(Map map)
        {
            List<Pawn> list = new List<Pawn>();
            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p == null || !p.Spawned || p.Dead || p.Downed) continue;
                    if (!p.HostileTo(Faction.OfPlayer)) continue;
                    if (!GenHostility.IsActiveThreatToPlayer(p)) continue;
                    list.Add(p);
                }
            }
            catch { }
            return list;
        }

        private static bool CanFight(Pawn p)
        {
            try
            {
                if (p == null || p.Dead || p.Downed || p.InMentalState || p.Drafted) return false;
                if (p.WorkTagIsDisabled(WorkTags.Violent)) return false;
                if (p.health == null || p.health.summaryHealth == null) return false;
                if (p.health.summaryHealth.SummaryHealthPercent < 0.4f) return false;
                if (p.health.hediffSet != null && p.health.hediffSet.BleedRateTotal > 0.06f) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool HasGun(Pawn p)
        {
            try
            {
                return p.equipment != null && p.equipment.Primary != null
                    && p.equipment.Primary.def.IsRangedWeapon;
            }
            catch { return false; }
        }

        private static void Defense(Map map, int tick)
        {
            if (tick < nextDefense) return;
            nextDefense = tick + 60;

            List<Pawn> hostiles = Hostiles(map);
            if (hostiles.Count == 0)
            {
                ReleaseBotDrafted(map);
                return;
            }

            List<Pawn> cols = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned) if (CanFight(p)) cols.Add(p);
            if (cols.Count == 0) return;

            IntVec3 rally = Infrastructure.Anchor(map);
            int gunners = 0;
            foreach (Pawn p in cols)
            {
                if (HasGun(p))
                {
                    gunners++;
                    Draft(p);
                    if (p.drafter != null) R.Set(p.drafter, "FireAtWill", true);
                    bool far = p.Position.DistanceToSquared(rally) > 81f;
                    if (far && p.CurJobDef != JobDefOf.Goto && p.CurJobDef != JobDefOf.AttackStatic)
                        p.jobs.StartJob(new Job(JobDefOf.Goto, rally), JobCondition.InterruptForced);
                    continue;
                }

                Pawn target = null;
                float best = float.MaxValue;
                foreach (Pawn h in hostiles)
                {
                    if (h == null || h.Dead) continue;
                    float d = h.Position.DistanceToSquared(p.Position);
                    if (d >= best) continue;
                    if (!p.CanReach(h, PathEndMode.Touch, Danger.Deadly)) continue;
                    best = d; target = h;
                }
                if (target == null) continue;
                Draft(p);
                if (p.CurJob == null || p.CurJob.def != JobDefOf.AttackMelee || p.CurJob.targetA.Thing != target)
                    p.jobs.StartJob(new Job(JobDefOf.AttackMelee, target), JobCondition.InterruptForced);
            }

            if (gunners == 0 && tick - lastThought > 3600)
            {
                lastThought = tick;
                Mind.Think("оборона", "Врагов " + hostiles.Count + ", огнестрельного оружия ни у кого нет — дерусь в ближнем бою и стараюсь одеть колонистов.");
            }
        }

        private static void Draft(Pawn p)
        {
            try
            {
                if (p.drafter == null) return;
                if (!p.Drafted)
                {
                    p.drafter.Drafted = true;
                    botDrafted.Add(p.thingIDNumber);
                }
                p.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
            }
            catch { }
        }

        private static void ReleaseBotDrafted(Map map)
        {
            if (botDrafted.Count == 0) return;
            List<int> ids = botDrafted.ToList();
            foreach (int id in ids)
            {
                Pawn p = null;
                foreach (Pawn c in map.mapPawns.FreeColonistsSpawned) if (c.thingIDNumber == id) { p = c; break; }
                if (p != null && p.drafter != null && p.Drafted) p.drafter.Drafted = false;
                botDrafted.Remove(id);
            }
            Mind.Think("оборона", "Угроза устранена: снимаю призыв и возвращаю колонистов к работе.");
        }

        /// <summary>Подготовка обороны: мини-турели у базы, если исследование уже есть.</summary>
        public static void DefensePrep(Map map)
        {
            if (map == null) return;
            try
            {
                int turrets = 0;
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    if (b.def.defName != null && b.def.defName.StartsWith("Turret_")) turrets++;
                int cap = 2 + map.mapPawns.FreeColonistsSpawnedCount / 3;
                if (turrets >= cap) return;
                ThingDef def = T.Def("Turret_MiniTurret");
                if (def == null || !Infrastructure.Available(def) || Infrastructure.Exists(map, def)) return;
                int pending = 0;
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                    if (t.def.entityDefToBuild == def) pending++;
                if (pending > 0) return;
                if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 4, 16, false))
                    Mind.Think("оборона", "Заложил мини-турель: огневая точка рядом с базой.");
            }
            catch (Exception e) { T.Warn("Подготовка обороны: " + e.Message); }
        }

        // ============================== еда ==============================

        public static int Hunt(Map map)
        {
            int marked = 0;
            try
            {
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Hunt) marked++;
                if (marked >= 3) return 0;
                IntVec3 anchor = Infrastructure.Anchor(map);
                List<Pawn> animals = new List<Pawn>();
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                {
                    if (a == null || !a.Spawned || a.Dead || a.Downed) continue;
                    if (a.RaceProps == null || !a.RaceProps.Animal || !a.RaceProps.IsFlesh) continue;
                    if (a.RaceProps.Insect) continue;
                    if (a.Faction == Faction.OfPlayer || a.HostFaction == Faction.OfPlayer) continue;
                    if (a.InAggroMentalState) continue;
                    if (map.designationManager.DesignationOn(a, DesignationDefOf.Hunt) != null) continue;
                    if (!a.Position.InHorDistOf(anchor, 55f)) continue;
                    animals.Add(a);
                }
                animals.Sort((a, b) => a.Position.DistanceToSquared(anchor).CompareTo(b.Position.DistanceToSquared(anchor)));
                int made = 0;
                for (int i = 0; i < animals.Count && made < 2; i++)
                {
                    map.designationManager.AddDesignation(new Designation(animals[i], DesignationDefOf.Hunt));
                    made++;
                }
                if (made > 0) Mind.Think("еда", "Пометил на охоту животных: " + made + " шт. Мясо нужно для готовки и на зиму.");
                return made;
            }
            catch (Exception e) { T.Warn("Охота: " + e.Message); return 0; }
        }

        private static void Farm(Map map)
        {
            try
            {
                int cells = 0;
                foreach (Zone z in map.zoneManager.AllZones) { Zone_Growing g = z as Zone_Growing; if (g != null) cells += g.cells.Count; }
                int want = 40 + map.mapPawns.FreeColonistsSpawnedCount * 10;
                if (cells >= want) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                List<IntVec3> pick = new List<IntVec3>();
                foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 20f, true))
                {
                    if (!c.InBounds(map) || c.Fogged(map)) continue;
                    if (map.zoneManager.ZoneAt(c) != null) continue;
                    if (c.Roofed(map)) continue;
                    if (map.fertilityGrid.FertilityAt(c) < 0.9f) continue;
                    bool blocked = false;
                    foreach (Thing t in map.thingGrid.ThingsAt(c)) if (!t.def.CanOverlapZones) { blocked = true; break; }
                    if (blocked) continue;
                    pick.Add(c);
                    if (pick.Count >= 24) break;
                }
                if (pick.Count < 6) return;

                Zone_Growing zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                for (int i = 0; i < pick.Count; i++) zone.AddCell(pick[i]);
                zone.allowSow = true;
                ThingDef crop = T.Def("Plant_Rice") ?? T.Def("Plant_Potato");
                if (crop != null) zone.SetPlantDefToGrow(crop);
                Mind.Think("еда", "Создал грядку на " + pick.Count + " клеток (" + (crop != null ? crop.label : "посевы") + "): теперь есть где выращивать еду.");
            }
            catch (Exception e) { T.Warn("Посевы: " + e.Message); }
        }

        private static void Bills(Map map)
        {
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    Building_WorkTable t = b as Building_WorkTable;
                    if (t == null || t.BillStack == null) continue;
                    string n = t.def.defName;
                    if (n == "TableButcher") EnsureBill(t, "ButcherCorpseFlesh", BillRepeatModeDefOf.RepeatCount, 10);
                    else if (n == "ElectricStove" || n == "FueledStove" || n == "Campfire")
                        EnsureBill(t, pop >= 6 ? "CookMealFine" : "CookMealSimple", BillRepeatModeDefOf.TargetCount, pop * 4 + 6);
                    else if (n == "TableStonecutter") EnsureBill(t, "MakeStoneBlocks", BillRepeatModeDefOf.TargetCount, 60);
                    else if (n == "TableMachining") EnsureBill(t, "Make_ComponentIndustrial", BillRepeatModeDefOf.TargetCount, 5);
                }
            }
            catch (Exception e) { T.Warn("Задания на верстаках: " + e.Message); }
        }

        private static void EnsureBill(Building_WorkTable table, string recipeName, BillRepeatModeDef mode, int count)
        {
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
            if (recipe == null) return;
            foreach (Bill b in table.BillStack.Bills) if (b.recipe == recipe) return;
            Bill_Production bill = recipe.MakeNewBill() as Bill_Production;
            if (bill == null) return;
            bill.repeatMode = mode;
            if (mode == BillRepeatModeDefOf.TargetCount) bill.targetCount = count;
            else bill.repeatCount = count;
            table.BillStack.AddBill(bill);
            Mind.Think("работа", "Добавил задание «" + recipe.label + "» на «" + table.def.label + "» (бот не найден — делаю сам).");
        }

        private static void Mine(Map map)
        {
            try
            {
                if (T.Count(map, ThingDefOf.Steel) >= 300) return;
                int mine = 0;
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Mine) mine++;
                if (mine >= 8) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                int made = 0;
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (made + mine >= 8) break;
                    if (t == null || !t.Spawned || t.Destroyed || !t.def.mineable) continue;
                    if (!t.Position.InHorDistOf(anchor, 45f)) continue;
                    if (map.designationManager.DesignationOn(t, DesignationDefOf.Mine) != null) continue;
                    map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.Mine));
                    made++;
                }
                if (made > 0) Mind.Think("работа", "Пометил на добычу " + made + " жил/залежей: нужна сталь для стройки и турелей.");
            }
            catch (Exception e) { T.Warn("Добыча: " + e.Message); }
        }

        public static int Tame(Map map)
        {
            try
            {
                int marked = 0;
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Tame) marked++;
                if (marked >= 2) return;
                int tamed = 0;
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                    if (a.RaceProps != null && a.RaceProps.Animal && a.Faction == Faction.OfPlayer) tamed++;
                if (tamed >= 4) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                int made = 0;
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                {
                    if (made >= 2) break;
                    if (a == null || !a.Spawned || a.Dead || a.Downed) continue;
                    if (a.RaceProps == null || !a.RaceProps.Animal || a.RaceProps.Insect) continue;
                    if (a.Faction != null || a.HostFaction != null) continue;
                    if (a.RaceProps.wildness > 0.65f) continue;
                    if (!a.Position.InHorDistOf(anchor, 45f)) continue;
                    if (map.designationManager.DesignationOn(a, DesignationDefOf.Tame) != null) continue;
                    map.designationManager.AddDesignation(new Designation(a, DesignationDefOf.Tame));
                    made++;
                }
                if (made > 0) Mind.Think("животные", "Пометил на приручение " + made + " животных: еда, шерсть и помощь в бою.");
                return made;
            }
            catch (Exception e) { T.Warn("Приручение: " + e.Message); return 0; }
        }

        private static void Prisoners(Map map)
        {
            try
            {
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                {
                    if (p == null || p.guest == null) continue;
                    R.Set(p.guest, "ExclusiveInteractionMode", PrisonerInteractionModeDefOf.AttemptRecruit);
                }
            }
            catch (Exception e) { T.Warn("Пленные: " + e.Message); }
        }

        private static void Equip(Map map)
        {
            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.Downed || p.Drafted || p.equipment == null || p.equipment.Primary != null) continue;
                    if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    if (p.CurJobDef == JobDefOf.Equip) continue;

                    Thing best = null; float score = -1f;
                    foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    {
                        if (t == null || !t.Spawned || t.Destroyed || t.IsForbidden(Faction.OfPlayer)) continue;
                        if (t.def == null || !t.def.IsWeapon) continue;
                        if (!p.CanReserve(t)) continue;
                        float v = t.MarketValue + (t.def.IsRangedWeapon ? 300f : 0f);
                        if (v <= score) continue;
                        score = v; best = t;
                    }
                    if (best == null) continue;
                    p.jobs.StartJob(new Job(JobDefOf.Equip, best), JobCondition.InterruptForced);
                    Mind.Think("оборона", p.LabelShort + " берёт «" + best.def.label + "»: без оружия колонию не защитить.");
                }
            }
            catch (Exception e) { T.Warn("Экипировка: " + e.Message); }
        }
    }
}
