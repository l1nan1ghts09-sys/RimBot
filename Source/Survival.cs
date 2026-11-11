using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Заменяет встроенные «энергетику» и «приоритеты работ» бота, исправляя их известные проблемы.</summary>
    public static class Survival
    {
        private static int nextPower, nextWork, nextSurvival;
        private static bool announced;

        public static void ResetRuntime()
        {
            nextPower = 0; nextWork = 0; nextSurvival = 0; announced = false;
            WorkGovernor.ResetRuntime();
            SurvivalTemplates.ResetRuntime();
        }

        public static void Run(Map map, int tick)
        {
            // Отключаем встроенные модули бота (его настройки autoPower / autoWorkPriorities).
            bool a = BotSettingsAccess.Set("autoPower", false);
            bool b = BotSettingsAccess.Set("autoWorkPriorities", false);
            if (!announced && (a || b))
            {
                announced = true;
                Mind.Think("решение", "Отключаю встроенные энергетику и приоритеты работ бота: они спамили генераторами и ставили всем приоритет 1 на всё. Беру управление на себя.");
            }
            try { Current.Game.playSettings.useWorkPriorities = true; } catch { }
            T.Safe("ThreatFix", () => ThreatFix.Install());
            T.Safe("Release", () => ThreatFix.ReleaseColonists(map, tick));
            if (tick >= nextPower) { nextPower = tick + 1200; T.Safe("Power", () => PowerManager.Run(map)); }
            if (tick >= nextWork) { nextWork = tick + 1500; T.Safe("Work", () => WorkGovernor.Run(map)); }
            if (tick >= nextSurvival) { nextSurvival = tick + 1800; T.Safe("SurvivalTemplates", () => SurvivalTemplates.Run(map, tick)); }
        }
    }

    // ====================== ЭНЕРГЕТИКА ======================
    public static class PowerManager
    {
        private static bool IsPlant(ThingDef d)
        {
            return d != null && d.comps != null && d.comps.Any(c => c.compClass != null && typeof(CompPowerPlant).IsAssignableFrom(c.compClass));
        }

        private static float Nominal(ThingDef d)
        {
            var p = d.GetCompProperties<CompProperties_Power>();
            float w = p != null ? Math.Abs(p.PowerConsumption) : 1000f;
            string n = d.defName.ToLowerInvariant();
            if (n.Contains("solar")) return w * 0.35f;     // только днём
            if (n.Contains("wind")) return w * 0.5f;
            return w;
        }

        private static IEnumerable<Thing> Pending(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                .Where(t => t.Faction == Faction.OfPlayer);
        }

        public static void Run(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawnedCount;
            var gens = map.listerBuildings.allBuildingsColonist.Where(x => x.GetComp<CompPowerPlant>() != null).ToList();
            var pendingGens = Pending(map).Where(t => IsPlant(t.def.entityDefToBuild as ThingDef)).ToList();
            int cap = Mathf.Clamp(2 + cols / 4, 2, 8);

            // 1) Чистка спама: максимум один недостроенный генератор, и не больше лимита в сумме.
            int allowedPending = Math.Max(0, Math.Min(1, cap - gens.Count));
            for (int i = allowedPending; i < pendingGens.Count; i++)
            {
                Thing t = pendingGens.OrderBy(x => x.thingIDNumber).ElementAt(i);
                if (!t.Destroyed) { t.Destroy(DestroyMode.Cancel); Mind.Think("энергия", "Отменил лишний чертёж генератора «" + t.def.entityDefToBuild.label + "» (уже " + gens.Count + " построено, лимит " + cap + ")."); }
            }
            pendingGens = Pending(map).Where(t => IsPlant(t.def.entityDefToBuild as ThingDef)).ToList();

            // 2) Лишние построенные генераторы без топлива — на разбор.
            if (gens.Count > cap + 1)
                foreach (Building g in gens.Where(x => { var r = x.GetComp<CompRefuelable>(); return r != null && !r.HasFuel; }).Skip(0).Take(gens.Count - cap - 1))
                    if (map.designationManager.DesignationOn(g, DesignationDefOf.Deconstruct) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(g, DesignationDefOf.Deconstruct));
                        Mind.Think("энергия", "Лишний генератор без топлива «" + g.LabelCap + "» помечен на разбор.");
                    }

            // 3) Подключение изолированных генераторов проводами.
            T.Safe("Conduits", () => Connect(map, gens));

            // 4) Баланс: мощность (с учётом незавершённых) против потребления.
            float demand = 0f;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                var pt = b.GetComp<CompPowerTrader>();
                if (pt == null || pt is CompPowerPlant) continue;
                demand += pt.Props.PowerConsumption * 0.7f;
            }
            float capacity = gens.Sum(g => Nominal(g.def)) + pendingGens.Sum(t => Nominal((ThingDef)t.def.entityDefToBuild));
            if (capacity >= demand * 1.15f + 200f) return;
            if (pendingGens.Count > 0 || gens.Count >= cap) return;

            ThingDef def = ChooseGenerator(map, gens);
            if (def == null) { Mind.Think("энергия", "Не хватает мощности (" + (int)capacity + "/" + (int)demand + " Вт), но подходящий генератор недоступен/нет топлива — жду."); return; }
            IntVec3 anchor = gens.Count > 0 ? gens[0].Position : NearestPowerCell(map);
            if (Infrastructure.PlaceNear(map, def, anchor, 3, 28, false))
                Mind.Think("энергия", "Мощность " + (int)capacity + " Вт при потреблении ~" + (int)demand + " Вт: строю один «" + def.label + "» (генераторов " + gens.Count + "/" + cap + ").");
        }

        private static IntVec3 NearestPowerCell(Map map)
        {
            var b = map.listerBuildings.allBuildingsColonist.FirstOrDefault(x => x.GetComp<CompPowerTrader>() != null);
            return b != null ? b.Position : Infrastructure.Anchor(map);
        }

        private static ThingDef ChooseGenerator(Map map, List<Building> gens)
        {
            int solar = gens.Count(g => g.def.defName.ToLowerInvariant().Contains("solar"));
            int wind = gens.Count(g => g.def.defName.ToLowerInvariant().Contains("wind"));
            int fuel = gens.Count(g => g.GetComp<CompRefuelable>() != null);
            int logs = T.Count(map, ThingDefOf.WoodLog);
            ThingDef d;
            d = T.Def("SolarGenerator"); if (Ok(d) && solar < 4) return d;
            d = T.Def("WindTurbine"); if (Ok(d) && wind < 3) return d;
            d = T.Def("WoodFiredGenerator"); if (Ok(d) && fuel < 2 && logs >= 150) return d;      // дровяные — только с запасом дров
            d = T.Def("ChemfuelPoweredGenerator"); if (Ok(d) && fuel < 2 && T.Count(map, T.Def("Chemfuel")) >= 100) return d;
            return null;
        }

        private static bool Ok(ThingDef d) { return d != null && d.BuildableByPlayer && Infrastructure.Available(d); }

        private static void Connect(Map map, List<Building> gens)
        {
            ThingDef conduit = T.Def("PowerConduit");
            if (conduit == null || Pending(map).Any(t => t.def.entityDefToBuild == conduit)) return;
            foreach (Building g in gens)
            {
                var pc = g.GetComp<CompPower>();
                if (pc == null || pc.PowerNet == null) continue;
                Building target = map.listerBuildings.allBuildingsColonist
                    .Where(b => { var c = b.GetComp<CompPower>(); return c != null && c.PowerNet != null && c.PowerNet != pc.PowerNet && (b.GetComp<CompPowerTrader>() != null || b.GetComp<CompPowerBattery>() != null) && !(b.GetComp<CompPowerPlant>() != null); })
                    .OrderBy(b => b.Position.DistanceToSquared(g.Position)).FirstOrDefault();
                if (target == null || target.Position.DistanceTo(g.Position) > 45f) continue;
                if (LayLine(map, conduit, g, target)) { Mind.Think("энергия", "Соединяю «" + g.LabelCap + "» проводами с «" + target.LabelCap + "»."); return; }
            }
        }

        private static bool LayLine(Map map, ThingDef conduit, Thing a, Thing b)
        {
            var cells = new List<IntVec3>();
            IntVec3 p = a.Position, q = b.Position;
            int x = p.x, z = p.z;
            while (x != q.x) { x += Math.Sign(q.x - x); cells.Add(new IntVec3(x, 0, z)); }
            while (z != q.z) { z += Math.Sign(q.z - z); cells.Add(new IntVec3(x, 0, z)); }
            var todo = new List<IntVec3>();
            foreach (IntVec3 c in cells)
            {
                if (!c.InBounds(map)) return false;
                if (a.OccupiedRect().Contains(c) || b.OccupiedRect().Contains(c)) continue;
                if (c.GetThingList(map).Any(t => t.def == conduit || t.def.entityDefToBuild == conduit)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(conduit, c, Rot4.North, map, false, null, null, null).Accepted) return false;
                todo.Add(c);
                if (todo.Count > 60) return false;
            }
            if (todo.Count == 0) return false;
            foreach (IntVec3 c in todo) GenConstruct.PlaceBlueprintForBuild(conduit, c, map, Rot4.North, Faction.OfPlayer, null);
            return true;
        }
    }

    // ====================== ПРИОРИТЕТЫ РАБОТ ======================
    public static class WorkGovernor
    {
        private static readonly string[] Vital = { "Doctor", "Cooking", "Growing" };
        private static int tickOfSummary = -1;

        public static void ResetRuntime() { tickOfSummary = -1; }

        public static void Run(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.skills != null).ToList();
            if (cols.Count == 0) return;

            // Состояние колонии -> срочность видов работ
            float nut = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (CountsAsHumanFood(d))
                    nut += T.Count(map, d) * d.GetStatValueAbstract(StatDefOf.Nutrition);
            float foodDays = nut / Math.Max(1f, cols.Count * 2.6f);
            int injured = cols.Count(p => p.health.HasHediffsNeedingTend() || p.Downed);
            int build = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer)
                      + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(t => t.Faction == Faction.OfPlayer);
            int damaged = map.listerBuildings.allBuildingsColonist.Count(b => b.def.useHitPoints
                && b.MaxHitPoints > 0 && b.HitPoints < b.MaxHitPoints * 0.9f);
            Agriculture.Report farm = null;
            try { farm = Agriculture.Analyze(map); } catch { }
            int farmCells = farm != null ? farm.cells : 0;
            int sowCells = farm != null ? farm.sow : 0;
            int ripeCells = farm != null ? farm.ripe : 0;
            int blighted = farm != null ? farm.blighted : 0;
            float farmFertility = farm != null ? farm.avgFertility : 0f;
            int farmTarget = farm != null ? farm.target : 0;
            if (farm == null)
            {
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    farmCells += g.cells.Count;
                    foreach (IntVec3 c in g.cells)
                    {
                        Plant plant = c.GetPlant(map);
                        if (plant == null || plant.Destroyed) sowCells++;
                        else if (plant.HarvestableNow) ripeCells++;
                    }
                }
            }
            int meals = 0;
            foreach (Thing t in map.listerThings.AllThings)
                if (t != null && t.def != null && t.def.defName != null && !t.IsForbidden(Faction.OfPlayer)
                    && t.def.defName.IndexOf("Meal", StringComparison.OrdinalIgnoreCase) >= 0)
                    meals += t.stackCount;
            float temp = 20f;
            try { temp = map.mapTemperature.OutdoorTemp; } catch { }
            bool cold = temp < 3f || (farm != null && farm.winter);
            bool hot = temp > 36f;
            int cutPending = map.designationManager.AllDesignations.Count(d => d.def == DesignationDefOf.CutPlant);
            bool prisoners = map.mapPawns.PrisonersOfColonySpawned.Any();
            int wood = T.Count(map, ThingDefOf.WoodLog);
            int steel = T.Count(map, ThingDefOf.Steel);
            int meds = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal);
            int craftBills = 0;
            foreach (Building_WorkTable t in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
                if (t.BillStack != null) craftBills += t.BillStack.Bills.Count;
            bool research = false;
            try { research = Find.ResearchManager != null && R.Get(Find.ResearchManager, "currentProj") != null; } catch { }

            float growUrg = farmCells == 0 ? 0.55f : ripeCells > 0 ? 1.0f : sowCells > 0 ? (foodDays < 10f ? 1.0f : 0.85f) : 0.45f;
            if (blighted > 0) growUrg = 1.0f;
            if (farmCells == 0 && foodDays < 8f) growUrg = Mathf.Max(growUrg, 0.85f);
            if (farmTarget > 0 && farmCells < farmTarget && foodDays < 12f) growUrg = Mathf.Max(growUrg, 0.9f);

            float cook = foodDays < 4f ? 1.0f : foodDays < 10f ? 0.85f : 0.6f;
            if (meals < cols.Count * 2 && nut > 0f) cook = Mathf.Max(cook, 0.9f);
            if (farm != null && farm.foodDays < 4f) cook = 1.0f;

            float tailor = cold ? 0.85f : craftBills > 0 ? 0.65f : 0.3f;
            float construct = build > 0 || cold ? 0.85f : 0.35f;
            float cut = foodDays < 3f || wood < 120 || cutPending > 0 || cold ? 0.85f : 0.25f;

            var urg = new Dictionary<string, float>
            {
                { "Doctor", injured > 0 ? 1.0f : 0.45f },
                { "Cooking", cook },
                { "Growing", growUrg },
                { "PlantCutting", cut },
                { "Hunting", foodDays < 4f ? 0.85f : cold && foodDays < 10f ? 0.5f : 0.1f },
                { "Construction", construct },
                { "Repair", damaged > 0 ? 0.85f : 0.10f },
                { "Hauling", foodDays < 3f ? 0.8f : 0.5f },
                { "Mining", steel < 200 ? 0.55f : 0.3f },
                { "Research", research ? 0.55f : 0.3f },
                { "Smithing", craftBills > 0 ? 0.65f : steel > 200 ? 0.3f : 0.15f },
                { "Tailoring", tailor },
                { "Crafting", craftBills > 0 ? 0.85f : 0.25f }, { "Art", 0.08f }, { "Cleaning", 0.28f },
                { "Warden", prisoners ? 0.5f : 0.05f }, { "Handling", 0.3f }, { "Childcare", 0.4f },
                { "BasicWorker", 0.35f }
            };
            if (meds < 8) urg["Crafting"] = Mathf.Max(urg["Crafting"], 0.55f);
            // Фокус: важные работы получают вес выше, остальные ниже — колонист не мечется.
            foreach (string key in urg.Keys.ToList())
                urg[key] = Mathf.Clamp01(urg[key] * Focus.WeightFor(key));

            // Базовые «всегда 1»
            string[] always = { "Firefighter", "Patient", "PatientBedRest" };
            var scores = new Dictionary<Pawn, List<KeyValuePair<WorkTypeDef, float>>>();
            foreach (Pawn p in cols)
            {
                foreach (string n in always) Set(p, n, 1);
                var list = new List<KeyValuePair<WorkTypeDef, float>>();
                foreach (var kv in urg)
                {
                    WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key);
                    if (wt == null || p.WorkTypeIsDisabled(wt)) continue;
                    float skill = Mathf.Clamp01(p.skills.AverageOfRelevantSkillsFor(wt) / 20f);
                    float passion = (int)p.skills.MaxPassionOfRelevantSkillsFor(wt) * 0.08f;
                    list.Add(new KeyValuePair<WorkTypeDef, float>(wt, kv.Value * (0.4f + 0.6f * skill) + passion));
                }
                scores[p] = list.OrderByDescending(x => x.Value).ToList();
            }

            // Распределяем: у каждого максимум два вида с приоритетом 1.
            foreach (Pawn p in cols)
            {
                var ranked = scores[p];
                for (int i = 0; i < ranked.Count; i++)
                    p.workSettings.SetPriority(ranked[i].Key, i < 1 ? 1 : i < 3 ? 2 : i < 6 ? 3 : 4);
            }

            // Жизненно важное должно быть закрыто: хотя бы один колонист с приоритетом ≤2.
            foreach (string name in Vital)
            {
                WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
                if (wt == null) continue;
                if (cols.Any(p => !p.WorkTypeIsDisabled(wt) && p.workSettings.GetPriority(wt) > 0 && p.workSettings.GetPriority(wt) <= 2)) continue;
                Pawn best = cols.Where(p => !p.WorkTypeIsDisabled(wt)).OrderByDescending(p => p.skills.AverageOfRelevantSkillsFor(wt)).FirstOrDefault();
                if (best == null) continue;
                best.workSettings.SetPriority(wt, 1);
                Mind.Think("работа", best.LabelShort + " назначен на «" + wt.labelShort + "» (приоритет 1): никто не закрывал жизненно важную работу.");
            }

            // Посадка зависит от задач: к посеву, к сбору урожая или всё уже засеяно.
            if (farmCells > 0 && (sowCells > 0 || ripeCells > 0))
            {
                WorkTypeDef grow = WorkTypeDefOf.Growing;
                if (grow != null)
                {
                    int need = Mathf.Clamp(1 + (sowCells + ripeCells) / 30, 1, Mathf.Max(1, cols.Count / 2));
                    var growers = cols.Where(p => !p.WorkTypeIsDisabled(grow))
                                      .OrderByDescending(p => p.skills.AverageOfRelevantSkillsFor(grow))
                                      .ToList();
                    for (int i = 0; i < growers.Count; i++)
                    {
                        int pr = i < need ? 1 : Math.Max(2, growers[i].workSettings.GetPriority(grow));
                        growers[i].workSettings.SetPriority(grow, pr);
                    }
                }
            }

            // Назначение специалистов под текущую задачу развития.
            if (research) Specialist(cols, WT("Research"));
            if (injured > 0) Specialist(cols, WT("Doctor"));
            if (foodDays < 4f || (meals < cols.Count * 2 && nut > 0f)) Specialist(cols, WT("Cooking"));
            if (build > 0 || cold || Defense.WantsBuilders) Specialist(cols, WT("Construction"));
            if (damaged > 0) Specialist(cols, WT("Repair"));
            if (craftBills > 0) Specialist(cols, WT("Crafting"));
            if (sowCells > 0 || ripeCells > 0 || blighted > 0) Specialist(cols, WT("Growing"));
            if (cold || wood < 120) Specialist(cols, WT("PlantCutting"));
            if (cold) Specialist(cols, WT("Tailoring"));

            if (tickOfSummary == -1 || Find.TickManager.TicksGame - tickOfSummary > 6000)
            {
                tickOfSummary = Find.TickManager.TicksGame;
                string farmInfo = farmCells > 0
                    ? ", грядок " + farmCells + "/" + farmTarget + " кл. (посеять " + sowCells + ", собрать " + ripeCells
                        + ", блайт " + blighted + ", плодородие " + Soil.Percent(farmFertility) + ")"
                    : ", грядок нет";
                Mind.Think("работа", "Приоритеты: еда " + foodDays.ToString("0.0") + " дн., раненых " + injured + ", строек " + build
                    + ", дерева " + wood + ", стали " + steel + ", повреждено построек " + damaged + farmInfo
                    + ". Каждому максимум 2 вида работ с приоритетом 1.");
            }
        }

        private static bool CountsAsHumanFood(ThingDef d)
        {
            if (d == null || d.ingestible == null || d.IsDrug || d.IsCorpse) return false;
            FoodTypeFlags ft = d.ingestible.foodType;
            if ((ft & FoodTypeFlags.Kibble) != 0) return false;
            if ((ft & FoodTypeFlags.Tree) != 0) return false;
            // Сырые овощи люди тоже едят; сено и киббл — нет. Это важно для реального
            // запаса дней: иначе бот паникует из-за «пустой» кладовой при полной грядке риса.
            string n = d.defName != null ? d.defName.ToLowerInvariant() : "";
            if (n.Contains("hay") || n.Contains("silage")) return false;
            return d.IsNutritionGivingIngestible && d.category == ThingCategory.Item;
        }

        private static WorkTypeDef WT(string name)
        {
            return DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
        }

        /// <summary>Если за работу никто не отвечает приоритетом 1 — назначаем лучшего по навыку.</summary>
        private static void Specialist(List<Pawn> cols, WorkTypeDef wt)
        {
            if (wt == null) return;
            if (cols.Any(p => !p.WorkTypeIsDisabled(wt) && p.workSettings.GetPriority(wt) == 1)) return;
            Pawn best = cols.Where(p => !p.WorkTypeIsDisabled(wt))
                            .OrderByDescending(p => p.skills.AverageOfRelevantSkillsFor(wt))
                            .FirstOrDefault();
            if (best != null) best.workSettings.SetPriority(wt, 1);
        }

        private static void Set(Pawn p, string def, int pr)
        {
            WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(def);
            if (wt != null && !p.WorkTypeIsDisabled(wt)) p.workSettings.SetPriority(wt, pr);
        }
    }

    // ====================== СТОРОЖ ЗАЦИКЛИВАНИЙ ======================
    public static class Watchdog
    {
        private class Track { public string key; public int since; }
        private class Rep { public int count; public int first; }
        private static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private static readonly Dictionary<string, Rep> reps = new Dictionary<string, Rep>();
        private static readonly Dictionary<Thing, int> unforbidAt = new Dictionary<Thing, int>();
        public static void ResetRuntime() { tracks.Clear(); reps.Clear(); unforbidAt.Clear(); }

        private static readonly string[] Exempt =
        {
            "Wait", "Wait_Combat", "Wait_Wander", "Wait_MaintainPosture", "LayDown", "Sleep", "Ingest", "Research", "DoBill", "Mine",
            "FinishFrame", "Repair", "Deconstruct", "Sow", "Harvest", "CutPlant", "HarvestTree", "Nuzzle", "Meditate", "Goto", "GotoWander", "AttackMelee", "AttackStatic", "Wear", "Equip"
        };

        private static string Key(Pawn p)
        {
            Job j = p.CurJob; if (j == null) return null;
            string t = j.targetA.HasThing ? j.targetA.Thing.thingIDNumber.ToString() : j.targetA.Cell.ToString();
            return j.def.defName + "@" + t;
        }

        public static void Sample(Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (var kv in unforbidAt.ToList())
                if (now >= kv.Value) { var f = kv.Key.TryGetComp<CompForbiddable>(); if (f != null) f.Forbidden = false; unforbidAt.Remove(kv.Key); }

            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed || p.jobs == null || p.Drafted) continue;
                string key = Key(p);
                if (key == null) { tracks.Remove(p.thingIDNumber); continue; }
                Track tr;
                if (!tracks.TryGetValue(p.thingIDNumber, out tr)) { tracks[p.thingIDNumber] = new Track { key = key, since = now }; continue; }
                if (tr.key != key)
                {
                    // новая задача: считаем повторные возвращения к одной и той же
                    string rk = p.thingIDNumber + ":" + key;
                    Rep r;
                    if (!reps.TryGetValue(rk, out r) || now - r.first > 6000) { r = new Rep { first = now }; reps[rk] = r; }
                    r.count++;
                    tr.key = key; tr.since = now;
                    if (r.count >= 7 && !Exempt.Contains(p.CurJob.def.defName)) { Break(p, "повторяет «" + p.CurJob.def.label + "» " + r.count + " раз подряд"); r.count = 0; }
                    continue;
                }
                // Одна и та же принудительная задача слишком долго
                if (p.CurJob.playerForced && now - tr.since > 1500 && !Exempt.Contains(p.CurJob.def.defName))
                {
                    Break(p, "принудительная задача «" + p.CurJob.def.label + "» длится " + (now - tr.since) + " тиков");
                    tr.since = now;
                }
            }
            if (reps.Count > 500) reps.Clear();
        }

        private static void Break(Pawn p, string why)
        {
            Job j = p.CurJob;
            Thing target = j != null && j.targetA.HasThing ? j.targetA.Thing : null;
            p.jobs.ClearQueuedJobs();
            p.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            string extra = "";
            if (target != null)
            {
                if (target is Blueprint || target is Frame) { target.Destroy(DestroyMode.Cancel); extra = " Цель была чертежом — отменён."; }
                else if (target.def.category == ThingCategory.Item)
                {
                    var f = target.TryGetComp<CompForbiddable>();
                    if (f != null) { f.Forbidden = true; unforbidAt[target] = Find.TickManager.TicksGame + 2500; extra = " Предмет «" + target.LabelShort + "» запрещён на час."; }
                }
            }
            Mind.Think("зависание", p.LabelShort + " " + why + " — прерываю." + extra);
        }
    }
}
