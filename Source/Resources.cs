using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Менеджмент ресурсов: сколько нужно держать в резерве, можно ли тратить
    /// на постройку/крафт и чем это обернётся, если запаса не останется.
    /// </summary>
    public static class Resources
    {
        public static int Count(Map map, ThingDef def) { return T.Count(map, def); }

        public static int Reserve(Map map, ThingDef def)
        {
            if (map == null || def == null) return 0;
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            TradeSettings s = TradeEmpireMod.S;
            if (def == ThingDefOf.Steel) return Math.Max(200, pop * 35);
            if (def == ThingDefOf.ComponentIndustrial) return Math.Max(12, pop * 3);
            if (def == ThingDefOf.MedicineIndustrial || def == ThingDefOf.MedicineHerbal)
                return Math.Max(8, pop * 2);
            if (def == ThingDefOf.WoodLog) return Math.Max(120, pop * 25);
            if (def == ThingDefOf.Silver) return s != null ? s.silverReserve : 400;
            string n = def.defName != null ? def.defName : "";
            if (n.StartsWith("Blocks")) return Math.Max(80, pop * 15);
            if (def.IsDrug) return 4;
            return 0;
        }

        /// <summary>Можно ли потратить qty с учётом резерва и срочности (0..1).</summary>
        public static bool CanSpend(Map map, ThingDef def, int qty, float urgency, out string why)
        {
            why = "";
            if (map == null || def == null || qty <= 0) return false;
            int have = T.Count(map, def);
            int reserve = Reserve(map, def);
            int effective = Math.Max(0, (int)(reserve * (1f - 0.65f * U.Clamp01(urgency))));
            if (have - qty < effective)
            {
                why = "«" + def.label + "» " + have + ", нужно " + qty + ", резерв " + effective
                    + " — трата оголит запас";
                return false;
            }
            why = "«" + def.label + "» " + have + " → останется " + (have - qty) + " (резерв " + effective + ")";
            return true;
        }

        /// <summary>Проверка стоимости постройки: материалы + Stuff.</summary>
        public static bool CanAfford(Map map, ThingDef def, ThingDef stuff, int count, float urgency, out string why)
        {
            why = "";
            if (map == null || def == null) return false;
            if (def.costList != null)
            {
                foreach (ThingDefCountClass c in def.costList)
                {
                    if (c == null || c.thingDef == null) continue;
                    if (!CanSpend(map, c.thingDef, c.count * Math.Max(1, count), urgency, out why)) return false;
                }
            }
            if (def.MadeFromStuff && stuff != null)
            {
                int n = 5;
                object v = R.Get(def, "costStuffCount");
                if (v != null) { try { n = Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { } }
                if (!CanSpend(map, stuff, Math.Max(1, n) * Math.Max(1, count), urgency, out why)) return false;
            }
            if (why.Length == 0) why = "материалы есть";
            return true;
        }

        /// <summary>Приблизительная рыночная стоимость постройки — для расчёта пользы/цены.</summary>
        public static float BuildCost(Map map, ThingDef def, ThingDef stuff, int count)
        {
            if (def == null) return 0f;
            float cost = 0f;
            if (def.costList != null)
                foreach (ThingDefCountClass c in def.costList)
                    if (c != null && c.thingDef != null) cost += c.thingDef.BaseMarketValue * c.count;
            if (def.MadeFromStuff && stuff != null)
            {
                int n = 5;
                object v = R.Get(def, "costStuffCount");
                if (v != null) { try { n = Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { } }
                cost += stuff.BaseMarketValue * Math.Max(1, n);
            }
            return cost * Math.Max(1, count);
        }

        public static string BudgetLine(Map map)
        {
            if (map == null) return "Ресурсы: карта не загружена.";
            return "Ресурсы: сталь " + T.Count(map, ThingDefOf.Steel) + "/резерв " + Reserve(map, ThingDefOf.Steel)
                + ", компоненты " + T.Count(map, ThingDefOf.ComponentIndustrial) + "/" + Reserve(map, ThingDefOf.ComponentIndustrial)
                + ", лекарства " + (T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal))
                + "/" + Reserve(map, ThingDefOf.MedicineIndustrial)
                + ", дерево " + T.Count(map, ThingDefOf.WoodLog) + "/" + Reserve(map, ThingDefOf.WoodLog)
                + ", серебро " + T.Count(map, ThingDefOf.Silver) + "/" + Reserve(map, ThingDefOf.Silver) + ".";
        }
    }
}
