using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Польза постройки: что она даёт колонии, сколько стоит и чем рискуем.</summary>
    public static class BuildValue
    {
        public class Evaluation
        {
            public ThingDef def;
            public float benefit, cost, risk, urgency, score;
            public string why = "";
            public string Line
            {
                get
                {
                    return (def != null ? def.label : "?") + ": польза " + U.Pct(U.Clamp01(benefit))
                        + ", цена " + U.Pct(U.Clamp01(cost)) + ", риск " + U.Pct(U.Clamp01(risk))
                        + ", счёт " + U.F(score, 2) + " — " + why;
                }
            }
        }

        public static Evaluation Evaluate(Map map, ThingDef def, ThingDef stuff = null)
        {
            Evaluation e = new Evaluation { def = def };
            if (map == null || def == null) return e;
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                int beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count();
                float threat = Utility.ThreatPressure(map);
                float production = T.Count(map, ThingDefOf.ComponentIndustrial) < 12 ? 0.8f : 0.45f;
                string dn = def.defName != null ? def.defName : "";

                if (def.thingClass != null && typeof(Building_Bed).IsAssignableFrom(def.thingClass))
                {
                    e.benefit = beds < pop ? 0.95f : 0.15f;
                    e.why = beds < pop ? "не хватает кроватей (" + beds + "/" + pop + ")" : "кроватей достаточно";
                }
                else if (def.thingClass != null && typeof(Building_Turret).IsAssignableFrom(def.thingClass))
                {
                    e.benefit = 0.45f + threat * 0.55f;
                    e.why = "турель закрывает сектор, угроза " + U.Pct(threat);
                }
                else if (def.thingClass != null && typeof(Building_Trap).IsAssignableFrom(def.thingClass))
                {
                    e.benefit = 0.40f + threat * 0.50f;
                    e.why = "ловушка на подходе, угроза " + U.Pct(threat);
                }
                else if (dn.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("Barricade", StringComparison.OrdinalIgnoreCase) >= 0
                    || def == ThingDefOf.Wall)
                {
                    e.benefit = 0.35f + threat * 0.50f;
                    e.why = "укрытие/стена для боя";
                }
                else if (def.thingClass != null && typeof(Building_WorkTable).IsAssignableFrom(def.thingClass))
                {
                    e.benefit = 0.35f + production * 0.35f;
                    e.why = "верстак для крафта и производства";
                }
                else if (dn == "CommsConsole" || dn == "OrbitalTradeBeacon")
                {
                    e.benefit = 0.55f;
                    e.why = "связь и орбитальная торговля";
                }
                else if (dn.IndexOf("Cooler", StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("Heater", StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("AirCondition", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    e.benefit = 0.45f;
                    e.why = "температура в помещении";
                }
                else
                {
                    e.benefit = 0.30f;
                    e.why = "полезная постройка";
                }

                ThingDef useStuff = stuff != null ? stuff : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                float raw = Resources.BuildCost(map, def, useStuff, 1);
                float wealth = 0f;
                try { wealth = map.wealthWatcher.WealthTotal; } catch { }
                e.cost = U.Clamp01(raw / Math.Max(1200f, wealth * 0.06f));
                e.risk = threat > 0.6f ? 0.18f : 0.05f;
                e.urgency = 0.75f + threat * 0.45f;
                e.score = e.benefit * e.urgency - e.cost - e.risk;
            }
            catch (Exception ex)
            {
                e.why = "оценка недоступна: " + ex.Message;
            }
            return e;
        }

        /// <summary>Короткая сводка пользы ключевых построек для панели.</summary>
        public static string Line(Map map)
        {
            if (map == null) return "Польза построек: карта не загружена.";
            var parts = new List<string>();
            Add(parts, map, T.Def("Bed"));
            Add(parts, map, T.Def("Turret_MiniTurret"));
            Add(parts, map, T.Def("TableMachining"));
            Add(parts, map, T.Def("CommsConsole"));
            return "Польза построек: " + string.Join("; ", parts.ToArray());
        }

        private static void Add(List<string> parts, Map map, ThingDef def)
        {
            if (def == null) return;
            Evaluation e = Evaluate(map, def);
            parts.Add(def.label + " " + U.F(e.score, 2));
        }
    }
}
