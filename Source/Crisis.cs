using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Антикризисные реакции. Каждая срабатывает с «остыванием» и пишется в дневник.</summary>
    public static class Crisis
    {
        private static readonly Dictionary<string, int> cooldown = new Dictionary<string, int>();

        public static void ResetRuntime() { cooldown.Clear(); }

        private static bool Ready(string key, int ticks)
        {
            int now = Find.TickManager.TicksGame, last;
            if (cooldown.TryGetValue(key, out last) && now - last < ticks) return false;
            cooldown[key] = now; return true;
        }

        public static void Run(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count == 0) return;
            Food(map, cols);
            Exposure(map, cols);
            Fire(map, cols);
        }

        private static float FoodDays(Map map, int colonists)
        {
            float nut = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (d.IsNutritionGivingIngestible && !d.IsDrug && d.category == ThingCategory.Item)
                    nut += T.Count(map, d) * d.GetStatValueAbstract(StatDefOf.Nutrition);
            return nut / Math.Max(1f, colonists * 2.6f);
        }

        private static void Food(Map map, List<Pawn> cols)
        {
            float days = FoodDays(map, cols.Count);
            bool starving = cols.Any(p => p.needs != null && p.needs.food != null && p.needs.food.CurLevelPercentage < 0.15f);
            if (days >= 3f && !starving) return;
            if (!Ready("food", 2500)) return;
            int n = Opening.DesignateWildFood(map, cols[0].Position, 14);
            BotSettingsAccess.Set("autoHunt", true);
            BotSettingsAccess.Set("autoCutPlants", true);
            Memory.Journal("КРИЗИС еды: запас на " + days.ToString("0.0") + " дн." + (starving ? ", есть голодающие" : "")
                + ". Помечено диких растений: " + n + ", включена охота.");
        }

        private static void Exposure(Map map, List<Pawn> cols)
        {
            float temp = map.mapTemperature.OutdoorTemp;
            bool cold = cols.Any(p => p.health.hediffSet.HasHediff(HediffDefOf.Hypothermia)) || temp < -5f && cols.Any(p => p.SafeTemperatureRange().min > temp - 10f && !p.Position.Roofed(map));
            bool hot = cols.Any(p => p.health.hediffSet.HasHediff(HediffDefOf.Heatstroke));
            if (!cold && !hot) return;
            if (!Ready("exposure", 5000)) return;
            if (cold)
            {
                ThingDef fire = T.Def("Campfire");
                bool have = fire != null && map.listerThings.ThingsOfDef(fire).Count > 0;
                if (fire != null && !have && Infrastructure.PlaceNear(map, fire, cols[0].Position, 2, 10, false))
                    Memory.Journal("КРИЗИС холода (" + temp.ToString("0") + "°C): закладываю костёр рядом с колонистами.");
                else Memory.Journal("КРИЗИС холода (" + temp.ToString("0") + "°C): есть переохлаждение, костёр уже есть/не поставить.");
            }
            if (hot) Memory.Journal("КРИЗИС жары: тепловой удар у колонистов. Работа в помещениях — через основной бот (Timetable/Health).");
        }

        private static void Fire(Map map, List<Pawn> cols)
        {
            var fire = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fire == null || fire.Count == 0) return;
            if (!Ready("fire", 1200)) return;
            foreach (Pawn p in cols)
                if (p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter))
                    p.workSettings.SetPriority(WorkTypeDefOf.Firefighter, 1);
            Memory.Journal("КРИЗИС пожара: огней " + fire.Count + ", тушение — приоритет 1 у всех.");
        }
    }

    /// <summary>Старт «Неприкрытая жестокость»: выживание с нуля — еда с земли, дерево, костёр, спальное место.</summary>
    public static class Opening
    {
        public static bool Brutal()
        {
            try
            {
                string n = (Find.Scenario != null ? Find.Scenario.name : "") ?? "";
                n = n.ToLowerInvariant();
                return n.Contains("naked") || n.Contains("brutal") || n.Contains("неприкрыт");
            }
            catch { return false; }
        }

        private static int next;

        public static void ResetRuntime() { next = 0; }

        public static void Run(Map map)
        {
            int tick = Find.TickManager.TicksGame;
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            bool early = tick < 15 * 60000 && (Brutal() || cols.Count <= 2);
            if (!early || tick < next || cols.Count == 0) return;
            next = tick + 1500;
            Pawn anchor = cols[0];
            int food = DesignateWildFood(map, anchor.Position, 16);
            int wood = DesignateTrees(map, anchor.Position, 5);
            ThingDef camp = T.Def("Campfire"), sleep = T.Def("SleepingSpot");
            string built = "";
            if (camp != null && map.listerThings.ThingsOfDef(camp).Count == 0 && !HasBlueprint(map, camp)
                && Infrastructure.PlaceNear(map, camp, anchor.Position, 3, 12, false)) built += " костёр";
            if (sleep != null)
            {
                int spots = map.listerThings.ThingsOfDef(sleep).Count;
                if (spots < cols.Count)
                {
                    int added = PlaceSpots(map, sleep, anchor.Position, cols.Count - spots);
                    if (added > 0) built += " спальных_мест:" + added;
                }
            }
            if (food + wood > 0 || built.Length > 0)
                Memory.Journal("СТАРТ с нуля: помечено еды " + food + ", деревьев " + wood + (built.Length > 0 ? ", заложено:" + built : "") + ".");
        }

        /// <summary>Спальное место — бесплатная мгновенная постройка без чертежа, поэтому ставим его напрямую.</summary>
        private static int PlaceSpots(Map map, ThingDef def, IntVec3 center, int count)
        {
            int n = 0;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, 9f, true))
            {
                if (n >= count) break;
                if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
                if (c.GetEdifice(map) != null) continue;
                bool busy = false;
                foreach (Thing t in map.thingGrid.ThingsAt(c))
                {
                    if (t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Pawn) { busy = true; break; }
                }
                if (busy) continue;
                try
                {
                    Thing spot = ThingMaker.MakeThing(def, null);
                    if (spot == null) break;
                    spot.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(spot, c, map, Rot4.North);
                    n++;
                }
                catch (Exception e) { T.Warn("Спальное место не поставлено: " + e.Message); break; }
            }
            if (n > 0) Mind.Think("быт", "Поставил спальных мест: " + n + " — колонистам не придётся спать на земле.");
            return n;
        }

        private static bool HasBlueprint(Map map, ThingDef d)
        {
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)) if (t.def.entityDefToBuild == d) return true;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)) if (t.def.entityDefToBuild == d) return true;
            return false;
        }

        public static int DesignateWildFood(Map map, IntVec3 center, int limit)
        {
            int n = 0;
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, 40f, true))
            {
                if (n >= limit) break;
                var p = t as Plant;
                if (p == null || p.def.plant == null || p.def.plant.Sowable || p.def.plant.IsTree) continue;
                ThingDef h = p.def.plant.harvestedThingDef;
                if (h == null || !h.IsNutritionGivingIngestible || !p.HarvestableNow) continue;
                if (map.designationManager.DesignationOn(p, DesignationDefOf.HarvestPlant) != null) continue;
                if (p.IsForbidden(Faction.OfPlayer)) continue;
                map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.HarvestPlant));
                n++;
            }
            return n;
        }

        public static int DesignateTrees(Map map, IntVec3 center, int limit)
        {
            if (T.Count(map, ThingDefOf.WoodLog) >= 80) return 0;
            int pending = map.designationManager.AllDesignations.Count(d => d.def == DesignationDefOf.CutPlant);
            if (pending >= limit) return 0;
            int n = 0;
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, 30f, true))
            {
                if (n + pending >= limit) break;
                var p = t as Plant;
                if (p == null || p.def.plant == null || !p.def.plant.IsTree || p.def.plant.harvestedThingDef != ThingDefOf.WoodLog) continue;
                if (map.designationManager.DesignationOn(p, DesignationDefOf.CutPlant) != null) continue;
                map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.CutPlant));
                n++;
            }
            return n;
        }
    }
}
