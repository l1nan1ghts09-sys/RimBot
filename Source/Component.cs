using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public class TradeComponent : GameComponent
    {
        public long totalSold, totalBought;
        public int deals;
        public int lastDealTick;
        private int nextRun;
        private int step;
        private int dialogSeenTick = -1;
        private Dictionary<int, int> lastTradedShip = new Dictionary<int, int>();
        private List<int> shipKeys, shipVals;
        public Dictionary<string, int> soldByDef = new Dictionary<string, int>();
        private List<string> soldKeys;
        private List<int> soldVals;

        public TradeComponent(Game game) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref totalSold, "totalSold");
            Scribe_Values.Look(ref totalBought, "totalBought");
            Scribe_Values.Look(ref deals, "deals");
            Scribe_Values.Look(ref lastDealTick, "lastDealTick");
            Scribe_Collections.Look(ref lastTradedShip, "lastTradedShip", LookMode.Value, LookMode.Value, ref shipKeys, ref shipVals);
            if (lastTradedShip == null) lastTradedShip = new Dictionary<int, int>();
            Scribe_Collections.Look(ref soldByDef, "soldByDef", LookMode.Value, LookMode.Value, ref soldKeys, ref soldVals);
            if (soldByDef == null) soldByDef = new Dictionary<string, int>();
        }

        public void RecordDeal(float sold, float bought)
        {
            totalSold += (long)sold; totalBought += (long)bought; deals++;
            lastDealTick = Find.TickManager.TicksGame;
        }

        public void RecordSale(ThingDef def, int count)
        {
            if (def == null || count <= 0) return;
            int cur;
            soldByDef.TryGetValue(def.defName, out cur);
            soldByDef[def.defName] = cur + count;
            if (TradeEmpireMod.S == null || TradeEmpireMod.S.economyLearning) Knowledge.ObserveSale(def.defName, count);
        }

        public override void GameComponentTick()
        {
            var s = TradeEmpireMod.S;
            if (s == null || !s.enabled) return;
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0) return;

            // Чистая ванильная игра: основной бот не установлен — ведём колонию сами.
            if (Vanilla.Active)
            {
                Map home = Find.AnyPlayerHomeMap;
                if (home != null) T.Safe("Vanilla", () => Vanilla.Run(home, tick));
            }

            // Открытое окно торговли (караван/гость/кем-то открытое): исполняем сделку сами.
            if (s.visitorTrade) T.Safe("OpenDialog", () => AutoTrader.HandleOpenDialog(ref dialogSeenTick));

            if (tick < nextRun) return;
            nextRun = tick + Math.Max(120, s.runInterval);
            if (!T.BotAllows()) return;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null || map.mapPawns.FreeColonistsSpawnedCount == 0) return;

            if (s.orbitalTrade) T.Safe("Orbital", () => OrbitalTrade(map));
            if (s.visitorTrade) T.Safe("Visitors", () => VisitorTrade(map));

            step++;
            switch (step % 3)
            {
                case 0: if (s.infrastructure) T.Safe("Infra", () => Infrastructure.Run(map)); break;
                case 1: if (s.production) T.Safe("Production", () => Planner.Run(map)); break;
                case 2: if (s.researchBoost) T.Safe("Research", ResearchBoost.InjectPriorities); break;
            }
        }

        private void OrbitalTrade(Map map)
        {
            if (map.passingShipManager == null || TradeSession.Active) return;
            if (!CommsConsoleUtility.PlayerHasPoweredCommsConsole(map)) return;
            int tick = Find.TickManager.TicksGame;
            foreach (PassingShip ship in map.passingShipManager.passingShips.ToList())
            {
                var trader = ship as ITrader;
                if (trader == null || !trader.CanTradeNow) continue;
                int last;
                int shipKey = ShipKey(ship);
                if (lastTradedShip.TryGetValue(shipKey, out last) && tick - last < 2500) continue;
                Pawn neg = AutoTrader.BestNegotiator(map);
                if (neg == null) return;
                bool done = AutoTrader.TradeWith(trader, neg, map);
                lastTradedShip[shipKey] = tick;
                if (done) return; // одна сделка за проход
            }
        }

        private static int ShipKey(PassingShip ship)
        {
            object id = R.Get(ship, "loadID");
            return id is int ? (int)id : ship.GetHashCode();
        }

        private void VisitorTrade(Map map)
        {
            // Торговцы-гости: отправляем лучшего переговорщика (само окно закроет HandleOpenDialog).
            if (TradeSession.Active) return;
            var job = DefDatabase<JobDef>.GetNamedSilentFail("TradeWithPawn");
            if (job == null) return;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Faction == null || p.Faction == Faction.OfPlayer || p.HostileTo(Faction.OfPlayer)) continue;
                if (p.TraderKind == null || !p.CanTradeNow) continue;
                Pawn neg = AutoTrader.BestNegotiator(map);
                if (neg == null || neg.jobs == null) return;
                if (neg.CurJob != null && neg.CurJob.def == job) return;
                int last;
                if (lastTradedShip.TryGetValue(p.thingIDNumber, out last) && Find.TickManager.TicksGame - last < 5000) continue;
                lastTradedShip[p.thingIDNumber] = Find.TickManager.TicksGame;
                var j = JobMaker.MakeJob(job, p);
                neg.jobs.TryTakeOrderedJob(j);
                return;
            }
        }
    }
}
