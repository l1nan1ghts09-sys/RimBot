using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public enum Stage { Early, Mid, Late }

    /// <summary>Профиль колониста для планирования: здоровье, потребности, сильная сторона.</summary>
    public class PawnProfile
    {
        public Pawn pawn;
        public string name;
        public bool downed, mental, bleeding, needsTend, hungry, exhausted, hypothermia, heatstroke;
        public float mood = 1f, rest = 1f, food = 1f, combat = 1f;
        public string bestRole = "", secondRole = "";
        public string note = "";

        public bool Capable { get { return !downed && !mental; } }
    }

    /// <summary>
    /// Один снимок колонии: то, что бот «видит» перед решением. Считается не каждый тик,
    /// а раз в несколько сотен тиков, и переиспользуется всеми модулями.
    /// </summary>
    public class ColonyWorld
    {
        public Map map;
        public int tick, day;
        public Stage stage;
        public string wealthTier = "?";

        // люди
        public List<Pawn> colonists = new List<Pawn>();
        public List<PawnProfile> profiles = new List<PawnProfile>();
        public int pop, downed, mental, bleeding, needsTend, hungry, starving, exhausted, hypothermia, heatstroke, idle;
        public float moodAvg = 1f, restMin = 1f, healthScore = 1f, foodNeedAvg = 1f;
        public float combatColony = 1f;

        // еда и ресурсы
        public float foodNutrition, growingNutrition, foodDays, foodDaysWithCrops, foodTrend;
        public float foodSafeDays, foodSpoilPct, foodSpoilNutrition, deteriorationValue;
        public int outdoorDeteriorating;
        public int medicine, medicinePotency, silver, steel, components, wood, cloth;
        public float wealth, wealthTrend;

        // угроза
        public bool threat;
        public int hostiles, hostilesNear;
        public float threatValue, threatRatio, nearestHostile = 999f, baseRisk, daysSinceThreat = 30f;

        // хозяйство
        public float powerCapacity, powerDemand, powerStored;
        public int beds, tables, tablesWithoutBills, bills, blueprints, frames, growingZones, prisoners, turrets, traps;
        public float outdoorTemp;
        public string weather = "—", biome = "—";
        public List<string> conditions = new List<string>();

        // наука/контекст
        public int researchDone, researchAvailable;
        public string researchCurrent = "—";
        public bool ideology, biotech;

        // производные
        public string ctx = "0000";
        public string goalCtx = "00";
        public float threatPressure, opportunity;
        public string headline = "";

        public float powerDeficit { get { return Mathf.Max(0f, powerDemand - powerCapacity); } }
        public float foodTargetDays { get { return Mathf.Clamp(8f + pop * 0.4f, 8f, 22f); } }
        public float healthUrgency { get { return U.Clamp01((downed * 0.8f + bleeding * 0.5f + needsTend * 0.3f) / Mathf.Max(1f, pop)); } }
        public float moodUrgency { get { return U.Clamp01((0.62f - moodAvg) * 2.4f + mental * 0.4f); } }

        public static ColonyWorld Build(Map map, ColonyWorld prev)
        {
            ColonyWorld w = new ColonyWorld();
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            w.map = map;
            w.tick = tick;
            w.day = tick / 60000;
            w.stage = w.day < 12 ? Stage.Early : w.day < 60 ? Stage.Mid : Stage.Late;
            w.biome = map.Biome != null ? map.Biome.defName : "?";

            w.ReadPeople();
            w.ReadResources();
            w.ReadThreat();
            w.ReadEconomy();
            w.ReadContext();

            // тренды и риск
            Timeline.Add(w);
            w.foodTrend = Timeline.FoodTrend();
            w.wealthTrend = Timeline.WealthTrend();
            w.daysSinceThreat = Timeline.DaysSinceThreat(w.day);
            w.baseRisk = Knowledge.RaidRisk(w);
            w.threatPressure = U.Clamp01(w.baseRisk * 0.65f + (w.threat ? 0.35f : 0f));
            w.opportunity = U.Clamp01((w.foodDays - 4f) / 12f) * U.Clamp01(1f - w.threatPressure) * U.Clamp01(w.healthScore);
            w.headline = w.BuildHeadline();

            return w;
        }

        // ---------------------------------------------------------------- люди
        private void ReadPeople()
        {
            colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            pop = colonists.Count;
            float moodSum = 0f, healthSum = 0f, foodSum = 0f;
            restMin = 1f;

            foreach (Pawn p in colonists)
            {
                PawnProfile pp = new PawnProfile();
                pp.pawn = p;
                pp.name = p.LabelShort;
                pp.downed = p.Downed;
                pp.mental = p.InMentalState;
                if (pp.downed) downed++;
                if (pp.mental) mental++;

                if (p.health != null && p.health.hediffSet != null)
                {
                    pp.bleeding = p.health.hediffSet.BleedRateTotal > 0.1f;
                    pp.needsTend = p.health.HasHediffsNeedingTend();
                    try { pp.hypothermia = p.health.hediffSet.HasHediff(HediffDefOf.Hypothermia); } catch { }
                    try { pp.heatstroke = p.health.hediffSet.HasHediff(HediffDefOf.Heatstroke); } catch { }
                }
                if (pp.bleeding) bleeding++;
                if (pp.needsTend) needsTend++;
                if (pp.hypothermia) hypothermia++;
                if (pp.heatstroke) heatstroke++;

                if (p.needs != null)
                {
                    if (p.needs.mood != null) pp.mood = p.needs.mood.CurLevelPercentage;
                    if (p.needs.rest != null) pp.rest = p.needs.rest.CurLevelPercentage;
                    if (p.needs.food != null) pp.food = p.needs.food.CurLevelPercentage;
                }
                else pp.mood = 0.7f;
                if (pp.food < 0.15f) hungry++;
                if (pp.food < 0.05f) starving++;
                if (pp.rest < 0.20f) exhausted++;
                restMin = Mathf.Min(restMin, pp.rest);

                pp.combat = BotBridge.PawnCombatValue(p);
                pp.bestRole = BestRole(p, false);
                pp.secondRole = BestRole(p, true);
                if (pp.downed) pp.note = "лежит";
                else if (pp.mental) pp.note = "срыв";
                else if (pp.bleeding) pp.note = "кровотечение";
                else if (pp.needsTend) pp.note = "нужен врач";
                else if (pp.food < 0.15f) pp.note = "голоден";

                moodSum += pp.mood;
                foodSum += pp.food;
                healthSum += pp.downed ? 0f : pp.mental ? 0.25f : pp.bleeding ? 0.55f : pp.needsTend ? 0.75f : 1f;
                profiles.Add(pp);
            }

            moodAvg = pop > 0 ? moodSum / pop : 1f;
            foodNeedAvg = pop > 0 ? foodSum / pop : 1f;
            healthScore = pop > 0 ? healthSum / pop : 1f;
            if (pop == 0) { moodAvg = 1f; restMin = 1f; healthScore = 0f; }
            idle = colonists.Count(p => p.jobs != null && p.jobs.curJob == null && !p.Downed && !p.InMentalState);
        }

        private static string BestRole(Pawn p, bool second)
        {
            try
            {
                if (p.skills == null) return "";
                WorkTypeDef best = null, next = null;
                float bestScore = -1f, nextScore = -1f;
                foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    if (wt == null || p.WorkTypeIsDisabled(wt)) continue;
                    float skill = p.skills.AverageOfRelevantSkillsFor(wt);
                    float passion = (int)p.skills.MaxPassionOfRelevantSkillsFor(wt) * 1.5f;
                    float sc = skill + passion;
                    if (sc > bestScore) { nextScore = bestScore; next = best; bestScore = sc; best = wt; }
                    else if (sc > nextScore) { nextScore = sc; next = wt; }
                }
                WorkTypeDef pick = second ? next : best;
                return pick != null ? pick.labelShort : "";
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- ресурсы
        private void ReadResources()
        {
            Dictionary<ThingDef, float> nut = DefExpert.NutritionMap;
            Dictionary<ThingDef, float> med = DefExpert.MedicineMap;
            float stored = 0f;

            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (t == null || t.def == null || t.Destroyed) continue;
                    float n;
                    if (nut.TryGetValue(t.def, out n))
                    {
                        if (!t.IsForbidden(Faction.OfPlayer)) stored += Mathf.Max(0f, n) * t.stackCount;
                        continue;
                    }
                    float potency;
                    if (med.TryGetValue(t.def, out potency))
                    {
                        if (!t.IsForbidden(Faction.OfPlayer))
                        {
                            medicine += t.stackCount;
                            medicinePotency += Mathf.RoundToInt(potency * 100f) * t.stackCount;
                        }
                    }
                }
            }
            catch (Exception e) { T.Warn("Обзор предметов неполный: " + e.Message); }

            foodNutrition = stored;
            foodDays = stored / Mathf.Max(1f, pop * 2.6f);
            try
            {
                FoodSafety.Report fs = FoodSafety.Analyze(map);
                foodSafeDays = fs.safeDays;
                foodSpoilPct = fs.spoilPct;
                foodSpoilNutrition = fs.spoilNutrition;
                Deterioration.Report ds = Deterioration.Analyze(map);
                outdoorDeteriorating = ds.count;
                deteriorationValue = ds.value;
            }
            catch (Exception e) { T.Warn("Прогноз порчи и ветшания: " + e.Message); }

            // будущий урожай: то, что уже растёт на грядках
            float growing = 0f;
            try
            {
                HashSet<IntVec3> zoneCells = new HashSet<IntVec3>();
                foreach (Zone_Growing z in map.zoneManager.AllZones.OfType<Zone_Growing>())
                {
                    if (z == null) continue;
                    growingZones++;
                    foreach (IntVec3 c in z.cells) zoneCells.Add(c);
                }
                if (zoneCells.Count > 0)
                {
                    foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
                    {
                        Plant plant = t as Plant;
                        if (plant == null || plant.def == null || plant.def.plant == null) continue;
                        if (!zoneCells.Contains(plant.Position)) continue;
                        ThingDef harvested = plant.def.plant.harvestedThingDef;
                        if (harvested == null) continue;
                        float hn;
                        if (!nut.TryGetValue(harvested, out hn)) continue;
                        float growth = Mathf.Clamp01(plant.Growth);
                        growing += Mathf.Max(0f, plant.def.plant.harvestYield) * growth * hn;
                    }
                }
            }
            catch (Exception e) { T.Warn("Оценка урожая неполная: " + e.Message); }

            growingNutrition = growing;
            foodDaysWithCrops = (stored + growing * 0.85f) / Mathf.Max(1f, pop * 2.6f);
            silver = T.Count(map, ThingDefOf.Silver);
            steel = T.Count(map, ThingDefOf.Steel);
            components = T.Count(map, ThingDefOf.ComponentIndustrial);
            wood = T.Count(map, ThingDefOf.WoodLog);
            cloth = T.Count(map, ThingDefOf.Cloth);
            if (medicine == 0)
            {
                medicine = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal) + T.Count(map, ThingDefOf.MedicineUltratech);
            }
        }

        // ---------------------------------------------------------------- угроза
        private void ReadThreat()
        {
            try { threat = GenHostility.AnyHostileActiveThreatToPlayer(map); } catch { threat = false; }
            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p == null || p.Dead || p.Downed || p.Faction == null) continue;
                    if (!p.Faction.HostileTo(Faction.OfPlayer)) continue;
                    if (p.HostFaction == Faction.OfPlayer || p.IsPrisonerOfColony) continue;
                    bool active;
                    try { active = GenHostility.IsActiveThreatToPlayer(p); } catch { active = true; }
                    if (!active) continue;

                    float d = NearestDistance(p);
                    if (d < nearestHostile) nearestHostile = d;
                    hostiles++;
                    if (d <= 60f) hostilesNear++;
                    if (d <= 120f) threatValue += BotBridge.PawnCombatValue(p);
                }
            }
            catch (Exception e) { T.Warn("Оценка угрозы неполная: " + e.Message); }

            if (hostiles > 0) threat = true;
            combatColony = BotBridge.ColonyCombatValue(map);
            float turretBonus = 0f;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null) continue;
                    if (b is Building_Turret) { turrets++; turretBonus += 2.5f; }
                    if (b is Building_Trap) { traps++; turretBonus += 0.35f; }
                }
            }
            catch { }
            combatColony = Mathf.Max(combatColony, pop * 1.2f + turretBonus);
            threatRatio = (threatValue + 0.1f) / Mathf.Max(0.5f, combatColony);
            if (nearestHostile > 900f) nearestHostile = 999f;
        }

        private float NearestDistance(Pawn p)
        {
            float best = 999f;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn c = colonists[i];
                if (c == null) continue;
                float d = p.Position.DistanceTo(c.Position);
                if (d < best) best = d;
            }
            return best;
        }

        // ---------------------------------------------------------------- хозяйство
        private void ReadEconomy()
        {
            wealth = 0f;
            try { wealth = map.wealthWatcher.WealthTotal; } catch { }

            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null) continue;
                    if (b is Building_Bed) beds++;
                    Building_WorkTable table = b as Building_WorkTable;
                    if (table != null)
                    {
                        tables++;
                        int billsOnTable = table.BillStack != null ? table.BillStack.Count : 0;
                        bills += billsOnTable;
                        if (billsOnTable == 0) tablesWithoutBills++;
                    }

                    CompPowerPlant plant = b.GetComp<CompPowerPlant>();
                    if (plant != null) powerCapacity += NominalPower(b);
                    CompPowerTrader trader = b.GetComp<CompPowerTrader>();
                    if (trader != null && plant == null)
                    {
                        try { powerDemand += Mathf.Max(0f, trader.Props.PowerConsumption); } catch { }
                    }
                    CompPowerBattery battery = b.GetComp<CompPowerBattery>();
                    if (battery != null)
                    {
                        object stored = R.Get(battery, "StoredEnergy");
                        if (stored is float) powerStored += (float)stored;
                    }
                }
            }
            catch (Exception e) { T.Warn("Обзор построек неполный: " + e.Message); }

            try
            {
                blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer);
                frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(t => t.Faction == Faction.OfPlayer);
            }
            catch { }

            try
            {
                prisoners = map.mapPawns.PrisonersOfColonySpawned.Count();
                conditions.Clear();
                foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
                    if (gc != null && gc.def != null) conditions.Add(gc.def.LabelCap.ToString());
            }
            catch { }

            try { outdoorTemp = map.mapTemperature.OutdoorTemp; } catch { outdoorTemp = 20f; }
            try
            {
                object cur = R.Get(map.weatherManager, "curWeather");
                object label = cur != null ? R.Get(cur, "label") : null;
                weather = label != null ? label.ToString() : "—";
            }
            catch { weather = "—"; }

            try
            {
                object rm = Find.ResearchManager;
                object cur = R.Get(rm, "currentProj");
                ResearchProjectDef project = cur as ResearchProjectDef;
                researchCurrent = project != null ? project.LabelCap.ToString() : "—";
                foreach (ResearchProjectDef p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                {
                    if (p == null) continue;
                    if (p.IsFinished) researchDone++;
                    else if (CanStart(p)) researchAvailable++;
                }
            }
            catch (Exception e) { T.Warn("Обзор исследований неполный: " + e.Message); }

            ideology = ModsConfig.IdeologyActive;
            try { biotech = R.IsTrue(R.CallStatic("Verse.ModsConfig", "get_BiotechActive")); } catch { biotech = false; }
            wealthTier = wealth < 3000f ? "бедная" : wealth < 15000f ? "развивающаяся" : wealth < 40000f ? "крепкая" : "богатая";
        }

        private float NominalPower(Building b)
        {
            try
            {
                CompPowerPlant plant = b.GetComp<CompPowerPlant>();
                object props = R.Get(plant, "Props");
                object pw = props != null ? R.Get(props, "basePowerGeneration") : null;
                if (!(pw is float)) pw = props != null ? R.Get(props, "PowerGeneration") : null;
                if (!(pw is float)) pw = props != null ? R.Get(props, "PowerConsumption") : null;
                if (!(pw is float)) pw = R.Get(plant, "PowerOutput");
                float gen = pw is float ? Math.Abs((float)pw) : 1000f;
                if (gen < 1f) gen = 1000f;
                string n = b.def.defName.ToLowerInvariant();
                if (n.Contains("solar")) return gen * 0.35f;
                if (n.Contains("wind")) return gen * 0.5f;
                return gen;
            }
            catch { return 1000f; }
        }

        public static bool CanStart(ResearchProjectDef p)
        {
            try
            {
                if (p == null || p.IsFinished) return false;
                if (p.prerequisites == null) return true;
                for (int i = 0; i < p.prerequisites.Count; i++)
                    if (p.prerequisites[i] != null && !p.prerequisites[i].IsFinished) return false;
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- контекст
        private void ReadContext()
        {
            int st = stage == Stage.Early ? 1 : stage == Stage.Mid ? 2 : 3;
            int th = !threat ? 0 : threatRatio > 1.15f ? 2 : 1;
            int fd = foodDays < 2f ? 0 : foodDays < 5f ? 1 : foodDays < 10f ? 2 : 3;
            int pp = pop <= 2 ? 0 : pop <= 5 ? 1 : pop <= 9 ? 2 : 3;
            ctx = "" + st + th + fd + pp;
            goalCtx = "" + st + th;
        }

        private string BuildHeadline()
        {
            if (pop == 0) return "Колонии больше нет — записываю разбор.";
            if (threat && threatRatio > 0.9f) return "Угроза: враги сильнее нас (" + hostiles + " на карте, " + U.Pct(threatRatio) + " от нашей силы).";
            if (foodDays < 2f || starving > 0) return "Голод: еды на " + U.F(foodDays) + " дн., голодающих " + hungry + ".";
            if (downed > 0 || bleeding > 0) return "Медицина: лежачих " + downed + ", кровотечений " + bleeding + ".";
            if (foodSpoilPct > 0.15f) return "Порча еды: сгниёт " + U.Pct(foodSpoilPct) + " запаса, до порчи успеем съесть "
                + U.F(foodSafeDays) + " дн. — нужен холод и долгий запас.";
            if (outdoorDeteriorating > 0) return "Ветшание: " + outdoorDeteriorating + " вещей на улице теряют прочность — убираю под крышу.";
            if (foodDays < foodTargetDays * 0.6f) return "Еда: запас " + U.F(foodDays) + " дн. при цели " + U.F(foodTargetDays) + " — расширяю производство.";
            if (moodUrgency > 0.25f) return "Настроение " + U.Pct(moodAvg) + ": риск срывов, даю отдых.";
            if (powerDeficit > 50f) return "Энергия: не хватает " + (int)powerDeficit + " Вт.";
            if (baseRisk > 0.6f) return "Скоро вероятен рейд (риск " + U.Pct(baseRisk) + ") — готовлю оборону.";
            return "Стабильно: развиваю экономику, науку и запас на будущее.";
        }

        public string Brief()
        {
            return "День " + day + ", " + pop + " чел., еда " + U.F(foodDays) + " дн. (" + U.Sign(foodTrend) + "/дн.), "
                + "угроза " + (threat ? hostiles + " врагов, " + U.Pct(threatRatio) + " силы" : "нет") + ", "
                + "настроение " + U.Pct(moodAvg) + ", энергия " + (int)powerCapacity + "/" + (int)powerDemand + " Вт, "
                + "богатство " + (int)wealth + " (" + U.Sign(wealthTrend) + "/дн.).";
        }
    }

    /// <summary>Короткая история наблюдений (в памяти): тренды еды/богатства, когда была последняя угроза.</summary>
    public static class Timeline
    {
        public class Point
        {
            public int tick, day;
            public float foodDays, wealth, mood, powerBalance, combat;
            public bool threat;
        }

        private static readonly List<Point> points = new List<Point>();
        private static int lastThreatDay = -1;
        private static int lastThreatTick = -1;
        private static Map lastMap;

        public static void Add(ColonyWorld w)
        {
            if (lastMap != null && w != null && w.map != lastMap) Clear();   // новая карта — новая история
            if (w != null) lastMap = w.map;
            Point last = points.Count > 0 ? points[points.Count - 1] : null;
            bool threatStarted = w.threat && (last == null || !last.threat);
            if (threatStarted) { lastThreatDay = w.day; lastThreatTick = w.tick; Knowledge.ObserveRaid(w); }
            if (last != null && w.tick - last.tick < 900)
            {
                last.foodDays = w.foodDays; last.wealth = w.wealth; last.mood = w.moodAvg; last.threat = w.threat;
                return;
            }
            points.Add(new Point { tick = w.tick, day = w.day, foodDays = w.foodDays, wealth = w.wealth, mood = w.moodAvg, threat = w.threat, powerBalance = w.powerCapacity - w.powerDemand, combat = w.combatColony });
            if (points.Count > 240) points.RemoveRange(0, 80);
        }

        public static float FoodTrend()
        {
            if (points.Count < 3) return 0f;
            List<Point> tail = points.Skip(Math.Max(0, points.Count - 6)).ToList();
            float dt = Mathf.Max(0.5f, (tail[tail.Count - 1].tick - tail[0].tick) / 60000f);
            return U.Slope(tail.Select(p => p.foodDays).ToList(), dt);
        }

        public static float WealthTrend()
        {
            if (points.Count < 3) return 0f;
            List<Point> tail = points.Skip(Math.Max(0, points.Count - 6)).ToList();
            float dt = Mathf.Max(0.5f, (tail[tail.Count - 1].tick - tail[0].tick) / 60000f);
            return U.Slope(tail.Select(p => p.wealth).ToList(), dt);
        }

        public static float DaysSinceThreat(int day)
        {
            int last = lastThreatDay >= 0 ? lastThreatDay : Knowledge.LastRaidDay;
            if (last > day) last = -1;                 // рейд из прошлой игры: отсчёт начинается заново
            if (last < 0) return Mathf.Min(60f, day);
            return Mathf.Max(0f, day - last);
        }

        public static Point Last { get { return points.Count > 0 ? points[points.Count - 1] : null; } }
        public static void Clear() { points.Clear(); lastThreatDay = -1; lastThreatTick = -1; lastMap = null; }
        public static int LastThreatTick { get { return lastThreatTick; } }
    }
}
