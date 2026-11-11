using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Крафт нужных вещей, а не только прибыльных: оружие, одежда, лекарства, компоненты.</summary>
    public static class Crafting
    {
        private static int nextRun;

        public static void ResetRuntime() { nextRun = 0; }

        public static void Run(Map map, int tick)
        {
            if (map == null || tick < nextRun) return;
            nextRun = tick + 9000;
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.craftItems) return;
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                int missingGuns = 0;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null || p.Dead || p.Downed || p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    if (p.equipment == null || p.equipment.Primary == null || !p.equipment.Primary.def.IsRangedWeapon) missingGuns++;
                }
                int meds = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal);
                int comps = T.Count(map, ThingDefOf.ComponentIndustrial);
                float temp = 21f;
                try { if (map.mapTemperature != null) temp = map.mapTemperature.OutdoorTemp; } catch { }
                bool cold = temp < 0f, heat = temp > 33f;

                if (missingGuns > 0) EnsureTable(map, "TableMachining");
                if (missingGuns > 0) EnsureTable(map, "TableSmithy");
                if (meds < 12) EnsureTable(map, "DrugLab");
                if (cold || heat) EnsureTable(map, "TableTailor");
                if (comps < 15) EnsureTable(map, "TableMachining");

                int made = 0;
                // Оружие: по одному лучшему рецепту, пока есть колонисты без стрелкового оружия.
                if (missingGuns > 0)
                    made += EnsureWeaponBill(map, missingGuns);

                foreach (Building_WorkTable table in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
                {
                    if (table == null || table.BillStack == null || table.def.AllRecipes == null) continue;
                    foreach (RecipeDef r in table.def.AllRecipes)
                    {
                        if (!r.AvailableNow || r.products == null || r.products.Count == 0) continue;
                        if (!CanDo(map, r)) continue;
                        ThingDef p = r.products[0].thingDef;
                        if (p == null) continue;

                        if (IsMedicine(p) && meds < 12)
                        {
                            if (EnsureBill(table, r, Math.Min(30, meds + 8), map)) made++;
                        }
                        else if (p == ThingDefOf.ComponentIndustrial && comps < 15)
                        {
                            if (EnsureBill(table, r, Math.Min(40, comps + 10), map)) made++;
                        }
                        else if ((cold || heat) && IsWeatherApparel(p))
                        {
                            if (EnsureBill(table, r, Math.Min(6, pop), map)) made++;
                        }
                    }
                }

                if (made > 0)
                    Mind.Think("крафт", "Добавил задания на нужные предметы: оружие/лекарства/компоненты/одежда. "
                        + "Оружия не хватает " + missingGuns + ", лекарств " + meds + ", компонентов " + comps + ".");
            }
            catch (Exception e) { T.Warn("Крафт: " + e.Message); }
        }

        private static int EnsureWeaponBill(Map map, int missing)
        {
            RecipeDef best = null; ThingDef bestProduct = null; float bestScore = -1f;
            foreach (Building_WorkTable table in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
            {
                if (table == null || table.def.AllRecipes == null) continue;
                foreach (RecipeDef r in table.def.AllRecipes)
                {
                    if (!r.AvailableNow || r.products == null || r.products.Count == 0) continue;
                    if (!CanDo(map, r)) continue;
                    ThingDef p = r.products[0].thingDef;
                    if (p == null || !p.IsRangedWeapon) continue;
                    float score = p.GetStatValueAbstract(StatDefOf.MarketValue);
                    if (score > bestScore) { bestScore = score; best = r; bestProduct = p; }
                }
            }
            if (best == null || bestProduct == null) return 0;
            Building_WorkTable targetTable = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                .FirstOrDefault(t => t.def.AllRecipes != null && t.def.AllRecipes.Contains(best));
            if (targetTable == null) return 0;
            int have = T.Count(map, bestProduct);
            int target = Math.Min(6, Math.Max(1, have + missing));
            return EnsureBill(targetTable, best, target, map) ? 1 : 0;
        }

        private static bool EnsureBill(Building_WorkTable table, RecipeDef recipe, int target, Map map)
        {
            if (table == null || recipe == null || table.BillStack == null) return false;
            if (table.BillStack.Bills.Any(b => b.recipe == recipe)) return false;
            TradeSettings s = TradeEmpireMod.S;
            if (s != null && table.BillStack.Count >= s.maxBillsPerTable) return false;
            Bill_Production bill = recipe.MakeNewBill() as Bill_Production;
            if (bill == null) return false;
            bill.repeatMode = BillRepeatModeDefOf.TargetCount;
            bill.targetCount = Math.Max(1, target);
            bill.pauseWhenSatisfied = true;
            bill.unpauseWhenYouHave = Math.Max(1, bill.targetCount * 3 / 4);
            table.BillStack.AddBill(bill);
            Mind.Think("крафт", "Задание «" + recipe.label + "» x" + bill.targetCount + " на «" + table.def.label + "»: делаю нужную вещь.");
            return true;
        }

        private static void EnsureTable(Map map, string defName)
        {
            ThingDef def = T.Def(defName);
            if (def == null || !Infrastructure.Available(def) || Infrastructure.Exists(map, def)) return;
            if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 3, 24, false))
                Mind.Think("крафт", "Построил верстак «" + def.label + "»: без него нужные предметы не сделать.");
        }

        private static bool CanDo(Map map, RecipeDef r)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p == null || p.Dead || p.Downed || p.skills == null) continue;
                if (r.requiredGiverWorkType != null && p.WorkTypeIsDisabled(r.requiredGiverWorkType)) continue;
                if (r.PawnSatisfiesSkillRequirements(p)) return true;
            }
            return false;
        }

        private static bool IsMedicine(ThingDef d)
        {
            return d == ThingDefOf.MedicineIndustrial || d == ThingDefOf.MedicineHerbal || d == ThingDefOf.MedicineUltratech;
        }

        private static bool IsWeatherApparel(ThingDef d)
        {
            if (d == null || d.defName == null) return false;
            return d.defName.IndexOf("Parka", StringComparison.OrdinalIgnoreCase) >= 0
                || d.defName.IndexOf("Duster", StringComparison.OrdinalIgnoreCase) >= 0
                || d.defName.IndexOf("Jacket", StringComparison.OrdinalIgnoreCase) >= 0
                || d.defName.IndexOf("Tuque", StringComparison.OrdinalIgnoreCase) >= 0
                || d.defName.IndexOf("Flak", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
