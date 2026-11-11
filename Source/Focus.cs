using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Один главный фокус за раз: оборона, еда, жильё, производство, наука, склад или торговля.
    /// Не даёт боту одновременно строить всё, ставить сотни заданий и разрываться между целями.
    /// </summary>
    public static class Focus
    {
        public enum Kind { Defense, Food, Shelter, Production, Research, Storage, Trade }

        public static Kind Current = Kind.Food;
        public static string Title = "Еда";
        public static string Why = "старт: сначала еда и кров";
        public static string Line = "Фокус: еда";
        public static int ChangedTick;

        private static int nextEval = -1;
        private static readonly Dictionary<Kind, float> scores = new Dictionary<Kind, float>();

        public static bool Active
        {
            get
            {
                TradeSettings s = TradeEmpireMod.S;
                return s != null && s.focusTasks;
            }
        }

        public static void ResetRuntime()
        {
            Current = Kind.Food;
            Title = "Еда";
            Why = "старт: сначала еда и кров";
            Line = "Фокус: еда";
            ChangedTick = 0;
            nextEval = -1;
            scores.Clear();
        }

        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            if (!Active) { Line = "Фокус: выключен (бот ведёт все задачи)"; return; }
            if (tick < nextEval) return;
            nextEval = tick + 1500;

            try
            {
                scores.Clear();
                float defense = ScoreDefense(map);
                float food = ScoreFood(map);
                float shelter = ScoreShelter(map);
                float production = ScoreProduction(map);
                float research = 0.42f;
                float storage = 0.22f;
                float trade = 0.25f;
                scores[Kind.Defense] = defense;
                scores[Kind.Food] = food;
                scores[Kind.Shelter] = shelter;
                scores[Kind.Production] = production;
                scores[Kind.Research] = research;
                scores[Kind.Storage] = storage;
                scores[Kind.Trade] = trade;

                Kind best = Kind.Food;
                float bestScore = food;
                foreach (var kv in scores)
                    if (kv.Value > bestScore) { best = kv.Key; bestScore = kv.Value; }

                // Гистерезис: не дёргаем фокус каждые полминуты, если преимущество небольшое.
                if (Current != Kind.Defense && best != Kind.Defense && tick - ChangedTick < 12000)
                {
                    float cur;
                    if (scores.TryGetValue(Current, out cur) && cur >= bestScore - 0.15f)
                        best = Current;
                }

                if (best != Current)
                {
                    Current = best;
                    ChangedTick = tick;
                    Title = Name(best);
                    Why = WhyFor(best, map);
                    Line = "Фокус: " + Title + " — " + Why;
                    Mind.Think("план", Line);
                }
                else
                {
                    Title = Name(best);
                    Why = WhyFor(best, map);
                    Line = "Фокус: " + Title + " — " + Why;
                }
            }
            catch (Exception e) { T.Warn("Фокус задач: " + e.Message); }
        }

        private static float ScoreDefense(Map map)
        {
            float s = 0.35f;
            try
            {
                if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return 2.0f;
                Defense.Report r = Defense.Last;
                if (r != null) s = 0.25f + r.difficulty * 1.05f;
            }
            catch { }
            return s;
        }

        private static float ScoreFood(Map map)
        {
            float days = 10f;
            try
            {
                BrainComponent b = BrainComponent.Get();
                if (b != null && b.World != null) days = b.World.foodDays;
            }
            catch { }
            if (days < 3f) return 1.6f;
            if (days < 7f) return 1.1f;
            if (days < 12f) return 0.7f;
            return 0.25f;
        }

        private static float ScoreShelter(Map map)
        {
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                int roofedBeds = 0;
                foreach (Building_Bed b in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
                    if (b.Position.Roofed(map)) roofedBeds++;
                if (roofedBeds < Math.Min(pop, 3)) return 1.05f;
            }
            catch { }
            return 0.20f;
        }

        private static float ScoreProduction(Map map)
        {
            float s = 0.25f;
            try
            {
                int tables = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Count();
                int bills = 0;
                foreach (Building_WorkTable t in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
                    if (t.BillStack != null) bills += t.BillStack.Bills.Count;
                if (tables == 0) s += 0.25f;          // нужен первый верстак
                if (bills > 0) s += 0.25f;            // есть что крафтить
                else if (tables > 0) s += 0.10f;      // верстаки простаивают
                if (T.Count(map, ThingDefOf.ComponentIndustrial) < 12) s += 0.15f;
                if (T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal) < 8) s += 0.15f;
                if (map.mapPawns.FreeColonistsSpawned.Any(p => p.equipment != null && p.equipment.Primary == null)) s += 0.15f;
            }
            catch { }
            return U.Clamp01(s);
        }

        private static string Name(Kind k)
        {
            switch (k)
            {
                case Kind.Defense: return "оборона";
                case Kind.Food: return "еда";
                case Kind.Shelter: return "жильё";
                case Kind.Production: return "производство";
                case Kind.Research: return "наука";
                case Kind.Storage: return "склад";
                default: return "торговля";
            }
        }

        private static string WhyFor(Kind k, Map map)
        {
            switch (k)
            {
                case Kind.Defense:
                    return Defense.Last != null ? "сложность " + Defense.Last.level + " (" + U.Pct(Defense.Last.difficulty) + "), риск " + U.Pct(Defense.Last.risk) : "готова оборона";
                case Kind.Food:
                    BrainComponent b = BrainComponent.Get();
                    return b != null && b.World != null ? "запас " + U.F(b.World.foodDays) + " дн." : "нужен запас еды";
                case Kind.Shelter:
                    return "нужны крытые кровати и помещение";
                case Kind.Production:
                    return "верстаки, задания и крафт нужных вещей";
                case Kind.Research:
                    return "очередь исследований";
                case Kind.Storage:
                    return "склады и порча";
                default:
                    return "торговля и сбыт";
            }
        }

        /// <summary>Разрешение подсистемам: не запускать всё сразу.</summary>
        public static bool Allows(string tag)
        {
            if (!Active) return true;
            switch (tag)
            {
                case "Food": return Current == Kind.Food;
                case "Shelter": return Current == Kind.Shelter;
                case "Craft": return Current == Kind.Food || Current == Kind.Production || Current == Kind.Defense;
                case "Build": return Current == Kind.Shelter || Current == Kind.Defense || Current == Kind.Production;
                case "Mine": return Current == Kind.Shelter || Current == Kind.Defense || Current == Kind.Production;
                case "Research": return Current == Kind.Research || Current == Kind.Production;
                case "Storage": return Current == Kind.Food || Current == Kind.Storage || Current == Kind.Production;
                case "Trade": return Current == Kind.Production || Current == Kind.Trade || Current == Kind.Storage || Current == Kind.Research;
                case "Defense": return true;
                default: return true;
            }
        }

        /// <summary>Вес вида работ: фокусные — выше, остальные — ниже, чтобы не распыляться.</summary>
        public static float WeightFor(string work)
        {
            if (!Active) return 1f;
            if (work == "Doctor" || work == "Firefighter" || work == "Patient" || work == "PatientBedRest") return 1f;
            bool focus = false;
            switch (Current)
            {
                case Kind.Food:
                    focus = work == "Cooking" || work == "Growing" || work == "Hunting" || work == "PlantCutting";
                    break;
                case Kind.Production:
                    focus = work == "Crafting" || work == "Smithing" || work == "Tailoring"
                        || work == "Construction" || work == "Mining" || work == "Hauling";
                    break;
                case Kind.Shelter:
                    focus = work == "Construction" || work == "Mining" || work == "Hauling";
                    break;
                case Kind.Defense:
                    focus = work == "Construction" || work == "Mining" || work == "Crafting";
                    break;
                case Kind.Research:
                    focus = work == "Research";
                    break;
                case Kind.Storage:
                case Kind.Trade:
                    focus = work == "Hauling" || work == "Crafting";
                    break;
            }
            return focus ? 1.35f : 0.80f;
        }
    }
}
