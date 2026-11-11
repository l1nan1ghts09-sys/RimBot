using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Ванильные механики, которые легко забыть в авторежиме: отдых и радость,
    /// красота, температура склада и еды, лечение, животные, пленные, идеология.
    /// </summary>
    public static class Mechanics
    {
        private static int next;
        private static string line = "Механики: отдых, климат и красота ещё не проверялись.";

        public static void ResetRuntime() { next = 0; line = "Механики: отдых, климат и красота ещё не проверялись."; }
        public static string Line(Map map) { return line; }

        public static void Run(Map map, int tick)
        {
            if (map == null || tick < next) return;
            next = tick + 9000;
            try
            {
                Recreation(map);
                Climate(map);
                Beauty(map);
                line = BuildLine(map);
            }
            catch (Exception e) { T.Warn("Механики: " + e.Message); }
        }

        // ------------------------------------------------------------ отдых
        private static void Recreation(Map map)
        {
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return;
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count == 0) return;
            bool lowJoy = false;
            foreach (Pawn p in cols)
            {
                try
                {
                    if (p.needs != null && p.needs.joy != null && p.needs.joy.CurLevelPercentage < 0.35f) { lowJoy = true; break; }
                }
                catch { }
            }
            int joy = map.listerBuildings.allBuildingsColonist.Count(b => IsJoy(b.def));
            if (!lowJoy && joy > 0) return;
            if (joy >= 2) return;

            ThingDef def = FirstAvailable("HorseshoesPin", "ChessTable", "BilliardsTable", "TubeTelevision", "PokerTable");
            if (def == null) return;
            if (!Utility.WeighOnly("joy_build", "построить место для отдыха", 0.55f, 0.08f, 0.02f, 1f,
                "колонисты смогут восстанавливать радость и настроение",
                "низкая радость приведёт к срывам и депрессии")) return;
            if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 3, 24, false))
                Mind.Think("отдых", "Строю «" + def.label + "»: в колонии " + joy + " мест отдыха, этого мало для " + cols.Count + " человек.");
        }

        private static bool IsJoy(Def def)
        {
            if (def == null || def.defName == null) return false;
            string n = def.defName;
            return n.IndexOf("Horseshoes", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Chess", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Billiards", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Television", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Poker", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Telescope", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("PartySpot", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------ климат
        private static void Climate(Map map)
        {
            Zone_Stockpile food = Storage.FoodZone(map);
            if (food == null || food.cells.Count == 0) return;
            float temp = 21f;
            try { temp = food.cells.First().GetTemperature(map); } catch { }
            if (temp > 4f && !Infrastructure.Exists(map, T.Def("Cooler")))
            {
                if (BuildClimate(map, "Cooler", -5f, food.cells.First(),
                    "в продуктовом складе " + Mathf.RoundToInt(temp) + "°C — еда портится; ставлю охладитель"))
                    return;
            }
            if (temp < 8f && !Infrastructure.Exists(map, T.Def("Heater")))
            {
                // Холод полезен для еды, но не для людей; греем жильё, а не морозилку.
                BuildClimate(map, "Heater", 21f, Infrastructure.Anchor(map),
                    "в жилье холодно; ставлю обогреватель у базы");
            }
        }

        private static bool BuildClimate(Map map, string defName, float target, IntVec3 at, string why)
        {
            ThingDef def = T.Def(defName);
            if (def == null || !Infrastructure.Available(def) || Infrastructure.Exists(map, def)) return false;
            if (!Utility.WeighOnly("climate_" + defName, "настроить температуру", 0.55f, 0.10f, 0.02f, 1f,
                "температура станет подходящей для людей и запасов", "порча еды или переохлаждение продолжатся")) return false;
            if (!Infrastructure.PlaceNear(map, def, at, 2, 12, true)) return false;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b.def != def) continue;
                    var c = b.GetComp<CompTempControl>();
                    if (c != null) { c.targetTemperature = target; break; }
                }
            }
            catch { }
            Mind.Think("климат", why + ". Цель " + Mathf.RoundToInt(target) + "°C.");
            return true;
        }

        // ------------------------------------------------------------ красота
        private static void Beauty(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count == 0) return;
            float mood = cols.Average(p => p.needs != null && p.needs.mood != null ? p.needs.mood.CurLevelPercentage : 0.6f);
            if (mood > 0.55f) return;
            if (map.listerBuildings.allBuildingsColonist.Any(b => b.def.defName != null && b.def.defName.StartsWith("Sculpture"))) return;
            ThingDef def = FirstAvailable("SculptureSmall", "SculptureGrand");
            if (def == null) return;
            if (!Utility.WeighOnly("art", "поставить скульптуру для настроения", 0.40f, 0.10f, 0.02f, 1f,
                "красота повысит настроение и снизит риск срывов", "мрачные комнаты продолжат давить на колонистов")) return;
            if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 3, 20, true))
                Mind.Think("красота", "Ставлю «" + def.label + "»: настроение " + U.Pct(mood) + ", нужна красота в комнатах.");
        }

        private static ThingDef FirstAvailable(params string[] names)
        {
            foreach (string n in names)
            {
                ThingDef d = T.Def(n);
                if (d != null && Infrastructure.Available(d)) return d;
            }
            return null;
        }

        private static string BuildLine(Map map)
        {
            int joy = map.listerBuildings.allBuildingsColonist.Count(b => IsJoy(b.def));
            int sculptures = map.listerBuildings.allBuildingsColonist.Count(b => b.def.defName != null && b.def.defName.StartsWith("Sculpture"));
            int coolers = map.listerBuildings.allBuildingsColonist.Count(b => b.def.defName != null && b.def.defName.IndexOf("Cooler", StringComparison.OrdinalIgnoreCase) >= 0);
            int heaters = map.listerBuildings.allBuildingsColonist.Count(b => b.def.defName != null && b.def.defName.IndexOf("Heater", StringComparison.OrdinalIgnoreCase) >= 0);
            return "Механики: отдых " + joy + ", скульптур " + sculptures + ", охладителей " + coolers
                + ", обогревателей " + heaters + "; лечение, животные, пленные и идеология — в профильных модулях.";
        }
    }
}
