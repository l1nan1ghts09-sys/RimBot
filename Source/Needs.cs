using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Потребности: гигиена (Dubs Bad Hygiene), настроение, вечеринки, идеология.</summary>
    public static class Needs
    {
        private static readonly Dictionary<string, int> cd = new Dictionary<string, int>();
        private static int lastParty = -999999;

        public static void ResetRuntime() { cd.Clear(); lastParty = -999999; }

        private static bool Ready(string k, int ticks)
        {
            int now = Find.TickManager.TicksGame, last;
            if (cd.TryGetValue(k, out last) && now - last < ticks) return false;
            cd[k] = now; return true;
        }

        public static void Run(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count == 0) return;
            Hygiene(map, cols);
            Mood(map, cols);
            Party(map, cols);
            Ideology(map, cols);
        }

        // ---------- гигиена ----------
        private static readonly string[] ToiletWords = { "latrine", "outhouse", "toilet", "bedpan" };
        private static readonly string[] WashWords = { "washbucket", "bucket", "basin", "shower", "bathtub", "bath" };

        private static bool NeedsPlumbing(ThingDef d)
        {
            if (d.comps == null) return false;
            return d.comps.Any(c => c.compClass != null && (c.compClass.Name.Contains("Pipe") || c.compClass.Name.Contains("Water") || c.compClass.Name.Contains("Sewage")));
        }

        private static ThingDef CheapestFixture(string[] words, Map map)
        {
            return DefDatabase<ThingDef>.AllDefs
                .Where(d => d.category == ThingCategory.Building && d.designationCategory != null
                    && words.Any(w => d.defName.ToLowerInvariant().Contains(w)) && Infrastructure.Available(d))
                .OrderBy(d => NeedsPlumbing(d) ? 1 : 0)
                .ThenBy(d => d.costList == null ? 0f : d.costList.Sum(c => c.thingDef.BaseMarketValue * c.count))
                .FirstOrDefault();
        }

        private static int CountFixtures(Map map, string[] words)
        {
            return map.listerBuildings.allBuildingsColonist.Count(b => words.Any(w => b.def.defName.ToLowerInvariant().Contains(w)));
        }

        private static void Hygiene(Map map, List<Pawn> cols)
        {
            NeedDef hyg = DefDatabase<NeedDef>.GetNamedSilentFail("Hygiene");
            NeedDef bladder = DefDatabase<NeedDef>.GetNamedSilentFail("Bladder");
            if (hyg == null && bladder == null) return;     // Dubs Bad Hygiene не подключён
            if (!Ready("hygiene", 7500)) return;
            int need = Mathf.CeilToInt(cols.Count / 3f);
            if (bladder != null && CountFixtures(map, ToiletWords) < need) Place(map, ToiletWords, cols, "туалетов", need);
            if (hyg != null && CountFixtures(map, WashWords) < need) Place(map, WashWords, cols, "умывальников", need);
        }

        private static void Place(Map map, string[] words, List<Pawn> cols, string what, int need)
        {
            ThingDef d = CheapestFixture(words, map);
            if (d == null) { Mind.Think("гигиена", "Нужно " + need + " " + what + ", но нужное исследование ещё не открыто."); return; }
            if (NeedsPlumbing(d)) { Mind.Think("гигиена", "Доступен только «" + d.label + "» с водопроводом — автоматически не строю, нужна ручная сантехника."); return; }
            if (map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Any(t => t.def.entityDefToBuild == d)) return;
            if (Infrastructure.PlaceNear(map, d, Infrastructure.Anchor(map), 3, 28, true))
                Mind.Think("гигиена", "Строю «" + d.label + "»: нужно " + need + " " + what + " на " + cols.Count + " колонистов.");
        }

        // ---------- настроение ----------
        private static readonly Dictionary<int, int> relaxed = new Dictionary<int, int>();

        private static void Mood(Map map, List<Pawn> cols)
        {
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return;
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in cols)
            {
                if (p.Downed || p.InMentalState || p.needs == null || p.needs.mood == null || p.timetable == null) continue;
                float lvl = p.needs.mood.CurLevelPercentage;
                if (lvl > 0.30f) continue;
                int last;
                if (relaxed.TryGetValue(p.thingIDNumber, out last) && now - last < 12000) continue;
                relaxed[p.thingIDNumber] = now;
                int hour = GenLocalDate.HourOfDay(p);
                for (int i = 0; i < 3; i++) p.timetable.SetAssignment((hour + i) % 24, TimeAssignmentDefOf.Joy);
                Mind.Think("решение", p.LabelShort + ": настроение " + (int)(lvl * 100) + "% — риск срыва, даю 3 часа на отдых.");
            }
        }

        // ---------- вечеринка ----------
        private static void Party(Map map, List<Pawn> cols)
        {
            int now = Find.TickManager.TicksGame;
            if (cols.Count < 5 || now - lastParty < 12 * 60000 || GenHostility.AnyHostileActiveThreatToPlayer(map)) return;
            float mood = cols.Average(p => p.needs != null && p.needs.mood != null ? p.needs.mood.CurLevelPercentage : 0.6f);
            if (mood > 0.65f && now - lastParty < 24 * 60000) return;
            ThingDef spot = T.Def("PartySpot");
            if (spot != null && map.listerThings.ThingsOfDef(spot).Count == 0 && Ready("partyspot", 20000))
                Infrastructure.PlaceNear(map, spot, Infrastructure.Anchor(map), 3, 20, false);
            GatheringDef g = DefDatabase<GatheringDef>.GetNamedSilentFail("Party");
            if (g == null || g.Worker == null) return;
            if (!Ready("party", 5000)) return;
            if (g.Worker.CanExecute(map, null) && g.Worker.TryExecute(map, null))
            {
                lastParty = now;
                Mind.Think("решение", "Устраиваю вечеринку: среднее настроение " + (int)(mood * 100) + "%, прошло давно.");
            }
        }

        // ---------- идеология ----------
        private static void Ideology(Map map, List<Pawn> cols)
        {
            if (!ModsConfig.IdeologyActive || !Ready("ideo", 15000)) return;
            Ideo ideo = cols.Select(p => p.Ideo).FirstOrDefault(i => i != null);
            if (ideo == null) return;
            foreach (Precept pr in ideo.PreceptsListForReading)
            {
                var pb = pr as Precept_Building;
                if (pb == null) continue;
                ThingDef d = pb.ThingDef;
                if (d == null || Infrastructure.Exists(map, d) || !Infrastructure.Available(d)) continue;
                if (Infrastructure.PlaceNear(map, d, Infrastructure.Anchor(map), 4, 28, d.building != null && d.building.isEdifice))
                {
                    Mind.Think("идеология", "Идеолигия «" + ideo.name + "» требует «" + d.label + "» — закладываю постройку.");
                    return;
                }
            }
        }
    }
}
