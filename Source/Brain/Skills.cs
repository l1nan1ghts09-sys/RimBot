using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public class PendingSkill
    {
        public string id = "";
        public string ctx = "";
        public int startTick;
        public int window;
        public MetricKind metric;
        public float startValue;
        public bool immediateSuccess;
        public bool threatAtStart;
    }

    /// <summary>
    /// Навыки — конкретные действия. Каждое действие измеряется: через окно времени бот смотрит,
    /// улучшилась ли метрика, и запоминает, насколько навык надёжен в таких условиях.
    /// </summary>
    public static class Skills
    {
        private static readonly List<Skill> all = new List<Skill>();
        private static readonly Dictionary<string, int> lastUse = new Dictionary<string, int>();
        private static readonly Dictionary<string, Breaker> breakers = new Dictionary<string, Breaker>();
        private static readonly List<PendingSkill> pending = new List<PendingSkill>();
        private static bool built;

        public static void Init()
        {
            if (built) return;
            built = true;

            // --- базовые, всегда полезные ---
            Add(new Skill("supervise", "Присмотр за подсистемами бота", "работа", GoalId.None, 240, 30000, MetricKind.General,
                w => w != null && w.pop > 0, Supervise));

            Add(new Skill("opening", "Старт с нуля: еда, дерево, костёр", "старт", GoalId.None, 2500, 12000, MetricKind.Food,
                w => w != null && w.pop > 0 && w.day < 15, OpeningSkill));

            Add(new Skill("events", "События, пришлые, квесты, письма", "событие", GoalId.None, 600, 12000, MetricKind.Population,
                w => w != null && w.pop > 0, EventsSkill));

            // --- пожар ---
            Add(new Skill("fire", "Тушение пожара", "пожар", GoalId.Fire, 900, 6000, MetricKind.Health,
                w => Fires(w) > 0, Fire));

            // --- оборона ---
            Add(new Skill("threat_response", "Реакция на угрозу", "оборона", GoalId.Threat, 600, 12000, MetricKind.Defense,
                w => w != null && w.threat, ThreatResponse));
            Add(new Skill("threat_release", "Возврат к работе после боя", "оборона", GoalId.Threat, 1800, 12000, MetricKind.Defense,
                w => w != null && !w.threat, ThreatRelease));
            Add(new Skill("defense_prep", "Подготовка обороны", "оборона", GoalId.DefensePrep, 5000, 60000, MetricKind.Defense,
                w => w != null && !w.threat && w.pop > 1, DefensePrep));

            // --- медицина ---
            Add(new Skill("triage", "Триаж и лечение", "медицина", GoalId.Medical, 1200, 12000, MetricKind.Health,
                w => w != null && (w.downed > 0 || w.bleeding > 0 || w.needsTend > 0), Triage));
            Add(new Skill("medical_stock", "Запас лекарств", "медицина", GoalId.Medical, 8000, 60000, MetricKind.Health,
                w => w != null && w.medicine < 8, MedicalStock));

            // --- еда ---
            Add(new Skill("food_emergency", "Аварийная еда", "еда", GoalId.Starvation, 2500, 10000, MetricKind.Food,
                w => w != null && (w.foodDays < 3f || w.starving > 0), FoodEmergency));
            Add(new Skill("food_security", "Продовольственная безопасность", "еда", GoalId.Food, 4000, 30000, MetricKind.Food,
                w => w != null && w.foodDays < w.foodTargetDays, FoodSecurity));
            Add(new Skill("food_market", "Не продавать еду в кризис", "торговля", GoalId.Food, 3000, 30000, MetricKind.Food,
                w => w != null && w.foodDays < w.foodTargetDays * 0.8f, HoldFood));
            Add(new Skill("cook", "Кухня и разделка", "еда", GoalId.Food, 10000, 60000, MetricKind.Food,
                w => w != null && w.pop > 1, Kitchen));

            // --- тепло ---
            Add(new Skill("warmth", "Тепло и защита от температуры", "тепло", GoalId.Warmth, 3000, 20000, MetricKind.Health,
                w => w != null && (w.outdoorTemp < -2f || w.outdoorTemp > 36f || w.hypothermia > 0 || w.heatstroke > 0), Warmth));

            // --- настроение/отдых ---
            Add(new Skill("needs", "Потребности, гигиена, вечеринка", "настроение", GoalId.Mood, 3000, 20000, MetricKind.Mood,
                w => w != null && w.pop > 0, NeedsSkill));
            Add(new Skill("rest", "Сон и восстановление", "отдых", GoalId.Rest, 3000, 12000, MetricKind.Mood,
                w => w != null && (w.restMin < 0.3f || w.exhausted > 0), RestSkill));

            // --- энергия ---
            Add(new Skill("power", "Энергосистема", "энергия", GoalId.Power, 1200, 30000, MetricKind.Power,
                w => w != null && w.powerDeficit > 0f, PowerSkill));

            // --- быт ---
            Add(new Skill("housing", "Кровати и базовые постройки", "быт", GoalId.Housing, 7000, 60000, MetricKind.Housing,
                w => w != null && w.beds < w.pop, Housing));
            Add(new Skill("clean", "Уборка", "быт", GoalId.Clean, 8000, 30000, MetricKind.Mood,
                w => w != null && w.pop > 1, Clean));

            // --- рельеф, интерфейс и бытовые паттерны ---
            Add(new Skill("cave_base", "Пещерная база: узкий вход и естественные стены", "быт", GoalId.Housing, 9000, 60000, MetricKind.Defense,
                w => w != null && w.map != null && (w.caveBase || w.hollowBase), CaveBasePattern));
            Add(new Skill("fire_safety", "Пожарная безопасность у батарей", "быт", GoalId.Housing, 12000, 90000, MetricKind.Housing,
                w => w != null && w.map != null && w.map.listerBuildings.allBuildingsColonist.Any(b =>
                    b != null && b.def != null && b.def.defName != null
                    && b.def.defName.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0), FireSafetyPattern));
            Add(new Skill("roof_clear", "Снять лишнюю крышу с панелей и грядок", "быт", GoalId.Housing, 10000, 60000, MetricKind.Housing,
                w => w != null && w.map != null && Roofs.NeedsWork(w.map), RoofClear));
            Add(new Skill("zone_cleanup", "Убрать пустые и дублирующиеся зоны", "быт", GoalId.Housing, 14000, 60000, MetricKind.Housing,
                w => w != null && w.map != null && Zones.HasObsolete(w.map), ZoneCleanup));

            // --- наука ---
            Add(new Skill("research", "Выбор исследования", "наука", GoalId.Research, 1500, 60000, MetricKind.Research,
                w => w != null && w.researchAvailable > 0, ResearchAdvisor.Step));
            Add(new Skill("research_guard", "Сторож исследований", "наука", GoalId.Research, 5000, 60000, MetricKind.Research,
                w => w != null && w.researchAvailable > 0, ResearchAdvisor.Guard));

            // --- экономика ---
            Add(new Skill("produce", "Прибыльные задания", "экономика", GoalId.Economy, 3000, 30000, MetricKind.Wealth,
                w => w != null && w.tables > 0, Produce));
            Add(new Skill("build", "Стройка для торговли", "экономика", GoalId.Economy, 5000, 60000, MetricKind.Wealth,
                w => w != null && w.pop > 1, Build));
            Add(new Skill("trade_policy", "Ценовая политика", "торговля", GoalId.Economy, 8000, 60000, MetricKind.Wealth,
                w => w != null && w.pop > 0, TradePolicy));

            // --- рост колонии ---
            Add(new Skill("recruit", "Рост колонии", "рост", GoalId.Recruit, 8000, 60000, MetricKind.Population,
                w => w != null && w.pop > 1, Recruit));
            Add(new Skill("prisoners", "Пленные", "рост", GoalId.Prisoners, 6000, 30000, MetricKind.Population,
                w => w != null && w.prisoners > 0, Prisoners));

            // --- прочее ---
            Add(new Skill("ideology", "Идеология", "идеология", GoalId.Ideology, 8000, 60000, MetricKind.Mood,
                w => w != null && w.ideology, IdeologySkill));
            Add(new Skill("animals", "Животные", "животные", GoalId.Animals, 12000, 90000, MetricKind.Food,
                w => w != null && w.pop > 2, Animals));
        }

        private static void Add(Skill s)
        {
            all.Add(s);
            if (!breakers.ContainsKey(s.id)) breakers[s.id] = new Breaker(s.title);
        }


        public static void Collect(ColonyWorld w)
        {
            if (w == null) return;
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.learning) { pending.Clear(); return; }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingSkill p = pending[i];
                int elapsed = w.tick - p.startTick;
                if (elapsed < p.window) continue;
                float now = Learning.MetricValue(w, p.metric);
                float gain;
                switch (p.metric)
                {
                    case MetricKind.Food: gain = (now - p.startValue) / 3f; break;
                    case MetricKind.Mood: gain = (now - p.startValue) / 2f; break;
                    case MetricKind.Health: gain = (now - p.startValue) / 2f; break;
                    case MetricKind.Defense: gain = (now - p.startValue) / 2f; break;
                    case MetricKind.Power: gain = (now - p.startValue) / 2f; break;
                    case MetricKind.Research: gain = (now - p.startValue) / 3f; break;
                    case MetricKind.Wealth: gain = (now - p.startValue) / 3f; break;
                    case MetricKind.Housing: gain = (now - p.startValue) / 2f; break;
                    case MetricKind.Production: gain = (now - p.startValue) / 4f; break;
                    case MetricKind.Population: gain = (now - p.startValue) / 3f; break;
                    default: gain = (now - p.startValue) / 4f; break;
                }
                gain = U.Clamp(gain, -1f, 1f);
                // при угрозе защитные метрики шумные: успех — не стало хуже
                bool success = p.immediateSuccess && (gain >= -0.05f || (p.threatAtStart && p.metric == MetricKind.Defense && gain > -0.5f));
                Knowledge.ObserveSkill(p.id, p.ctx, success, gain);   // внутри пишется и контекст, и «*»
                if (LessonWorthy(p.id, p.ctx))
                {
                    float rate = Reliability(p.id, p.ctx);
                    if (rate < 0.35f)
                        Knowledge.AddLesson("skill:" + p.id, "Навык «" + p.id + "» редко помогает в текущих условиях (успех " + U.Pct(rate)
                            + ") — бот переключается на другие действия.", 0.55f, p.ctx, GoalId.None, 0f);
                }
                pending.RemoveAt(i);
            }
            if (pending.Count > 40) pending.RemoveRange(0, pending.Count - 40);
        }

        private static bool LessonWorthy(string id, string ctx)
        {
            int n = 0;
            string prefix = id + "|" + ctx;
            foreach (var kv in Knowledge.Skills) if (kv.Key == prefix) n = kv.Value.n;
            return n > 0 && n % 4 == 0;
        }

        private static void Track(string id, ColonyWorld w, SkillResult res, int window, MetricKind metric)
        {
            if (!res.ran) return;
            if (metric == MetricKind.General) return;      // общие действия не измеряем: шум
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.learning) return;
            if (!res.success)
            {
                Knowledge.ObserveSkill(id, w.ctx, false, 0);
                return;
            }
            PendingSkill p = new PendingSkill();
            p.id = id;
            p.ctx = w.ctx;
            p.startTick = w.tick;
            p.window = Math.Max(3000, window);
            p.metric = metric;
            p.startValue = Learning.MetricValue(w, metric);
            p.immediateSuccess = true;
            p.threatAtStart = w.threat;
            pending.Add(p);
        }

        public static float Reliability(string id, string ctx)
        {
            LearnedSkill local;
            float rate = 0.5f;
            int n = 0;
            if (Knowledge.Skills.TryGetValue(id + "|" + ctx, out local) && local.n > 0) { rate = local.Rate; n = local.n; }
            LearnedSkill star;
            if (Knowledge.Skills.TryGetValue(id + "|*", out star) && star.n > 0)
            {
                rate = (rate * (n + 1f) + star.Rate * 2f) / (n + 3f);
                n += star.n;
            }
            return n == 0 ? 0.5f : rate;
        }

        public static int Uses(string id)
        {
            int n = 0;
            foreach (var kv in Knowledge.Skills) if (kv.Key.EndsWith("|*", StringComparison.Ordinal) && kv.Key.StartsWith(id + "|", StringComparison.Ordinal)) n = kv.Value.n;
            return n;
        }

        private static bool Ready(Skill s, int tick)
        {
            int last;
            if (lastUse.TryGetValue(s.id, out last) && tick - last < s.cooldown) return false;
            return true;
        }

        /// <summary>Глобальные навыки: присмотр, старт, события. Работают независимо от выбранной цели.</summary>
        public static void RunGlobal(ColonyWorld w, int budget)
        {
            Init();
            if (w == null) return;
            Collect(w);
            int ran = 0;
            foreach (Skill s in all)
            {
                if (s.goal != GoalId.None) continue;
                if (!Ready(s, w.tick)) continue;
                if (s.can != null && !s.can(w)) continue;
                if (ran >= budget) break;
                if (RunOne(s, w)) ran++;
            }
        }

        /// <summary>Навыки под текущие цели: сначала самая срочная, бюджет действий ограничен.</summary>
        public static void RunForGoals(ColonyWorld w, List<ColonyGoal> goals, int budget)
        {
            Init();
            if (w == null || goals == null) return;
            Collect(w);
            int ran = 0;
            foreach (ColonyGoal g in goals)
            {
                if (ran >= budget) break;
                if (g.utility < 0.22f) break;
                foreach (Skill s in all)
                {
                    if (s.goal != g.id) continue;
                    if (ran >= budget) break;
                    if (!Ready(s, w.tick)) continue;
                    if (s.can != null && !s.can(w)) continue;
                    float rel = Reliability(s.id, w.ctx);
                    if (rel < 0.25f && Uses(s.id) >= 5) continue;   // выученная бесполезность
                    if (RunOne(s, w)) ran++;
                }
            }
        }

        private static bool RunOne(Skill s, ColonyWorld w)
        {
            lastUse[s.id] = w.tick;
            Breaker b;
            if (!breakers.TryGetValue(s.id, out b)) { b = new Breaker(s.title); breakers[s.id] = b; }
            SkillResult res = null;
            b.Run(() => { res = s.run != null ? s.run(w) : SkillResult.No("нет реализации"); }, s.cooldown);
            if (res == null) return false;
            if (!res.ran)
            {
                if (!string.IsNullOrEmpty(res.message)) Mind.Think(s.category, res.message);
                return false;
            }
            MetricKind metric = res.metric;
            int window = res.window > 0 ? res.window : s.window;
            Track(s.id, w, res, window, metric);
            if (!string.IsNullOrEmpty(res.message)) Mind.Think(s.category, res.message);
            if (res.ran && res.success && res.message.Length > 40 && TradeEmpireMod.S != null && TradeEmpireMod.S.verboseWhy)
                Knowledge.Journal("НАВЫК «" + s.title + "»: " + res.message);
            return true;
        }

        // ================================================================== реализация навыков

        private static int Fires(ColonyWorld w)
        {
            try { return w.map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count; } catch { return 0; }
        }

        private static SkillResult Supervise(ColonyWorld w)
        {
            var s = TradeEmpireMod.S;
            if (w.foodDays > w.foodTargetDays) Policy.HoldFood = false;   // запас восстановлен — снова можно торговать едой
            if (s != null && s.takeover) Survival.Run(w.map, w.tick);
            else
            {
                T.Safe("ThreatFix", () => ThreatFix.Install());
                ThreatFix.ReleaseColonists(w.map, w.tick);
            }
            return SkillResult.Yes("", MetricKind.General, 30000);
        }

        private static SkillResult OpeningSkill(ColonyWorld w)
        {
            Opening.Run(w.map);
            return SkillResult.Yes("Старт с нуля: проверяю дикую еду, деревья, костёр и спальные места.", MetricKind.Food, 12000);
        }

        private static SkillResult EventsSkill(ColonyWorld w)
        {
            Events.Run(w.map);
            return SkillResult.No("");
        }

        private static SkillResult Fire(ColonyWorld w)
        {
            Crisis.Run(w.map);
            int n = Fires(w);
            return SkillResult.Yes("Пожар: " + n + " очагов, тушение приоритетом 1.", MetricKind.Health, 6000);
        }

        private static SkillResult ThreatResponse(ColonyWorld w)
        {
            var s = TradeEmpireMod.S;
            if (s == null) return SkillResult.No("");
            if (Vanilla.Active) { Vanilla.DefensePrep(w.map); Vanilla.Hunt(w.map); }
            BotBridge.SetBotSetting("autoDraftOnRaid", true);
            bool flee = w.threatRatio > 1.15f || w.healthScore < 0.7f;
            BotBridge.SetBotSetting("autoFlee", flee);
            string msg = "Угроза: " + w.hostiles + " врагов (сила " + U.Pct(w.threatRatio) + " от нашей). "
                + (flee ? "Слабых и раненых уводим, включаю бегство." : "Держим позиции, бегство выключено.");
            if (w.threatRatio > 1.5f && w.medicine < 6)
                msg += " Перевес врага большой при нехватке лекарств — приоритет жизни, а не имущества.";
            return SkillResult.Yes(msg, MetricKind.Defense, 12000);
        }

        private static SkillResult ThreatRelease(ColonyWorld w)
        {
            ThreatFix.ReleaseColonists(w.map, w.tick);
            return SkillResult.Yes("", MetricKind.Defense, 12000);
        }

        private static SkillResult DefensePrep(ColonyWorld w)
        {
            if (Vanilla.Active) Vanilla.DefensePrep(w.map);
            BotBridge.SetBotSetting("autoDefense", true);
            string msg = "Готовлю оборону: риск рейда " + U.Pct(w.baseRisk) + ", турелей " + w.turrets
                + ", ловушек " + w.traps + ", с прошлого рейда " + U.F(w.daysSinceThreat) + " дн.";
            if (w.turrets == 0 && w.threatPressure > 0.5f)
                msg += " Турелей нет — держу приоритет на исследовании/постройке обороны.";
            return SkillResult.Yes(msg, MetricKind.Defense, 60000);
        }

        private static SkillResult Triage(ColonyWorld w)
        {
            // Расписание не трогаем: лечением и постельным режимом занимается основной бот,
            // а самодельные смены сна только мешают врачам.
            BotBridge.SetBotSetting("autoMedical", true);
            string msg = "Медицина: лежачих " + w.downed + ", кровотечений " + w.bleeding + ", нужен врач " + w.needsTend
                + ", лекарств " + w.medicine + ".";
            if (w.medicine < 6) msg += " Лекарств мало — включаю закупку/производство.";
            return SkillResult.Yes(msg, MetricKind.Health, 12000);
        }

        private static SkillResult MedicalStock(ColonyWorld w)
        {
            if (w.medicine < 8) BotBridge.SetBotSetting("autoMedical", true);
            if (TradeEmpireMod.S != null) TradeEmpireMod.S.maxBuyRatio = Mathf.Max(TradeEmpireMod.S.maxBuyRatio, 2.4f);
            return SkillResult.Yes("Лекарств " + w.medicine + " (сила " + w.medicinePotency + "): держу закупку и производство в приоритете.",
                MetricKind.Health, 30000);
        }

        private static SkillResult FoodEmergency(ColonyWorld w)
        {
            Policy.HoldFood = true;
            Crisis.Run(w.map);
            int wild = 0, fields = 0;
            try
            {
                Pawn anchor = w.colonists.FirstOrDefault();
                if (anchor != null) wild = Opening.DesignateWildFood(w.map, anchor.Position, 24);
                fields = Agriculture.EnsureFields(w.map, true);
            }
            catch { }
            if (Vanilla.Active) Vanilla.Hunt(w.map);
            BotBridge.SetBotSetting("autoHunt", true);
            BotBridge.SetBotSetting("autoCutPlants", true);
            BotBridge.SetBotSetting("autoGrowing", true);
            return SkillResult.Yes("Аварийная еда: запас " + U.F(w.foodDays) + " дн., голодают " + w.starving
                + ". Помечено дикой еды " + wild + ", грядки +" + fields
                + ", охота и рубка включены, продажа еды запрещена.", MetricKind.Food, 10000);
        }

        private static SkillResult FoodSecurity(ColonyWorld w)
        {
            Policy.HoldFood = w.foodDays < 4f;
            BotBridge.SetBotSetting("autoGrowing", true);
            if (w.foodDays < w.foodTargetDays * 0.5f) BotBridge.SetBotSetting("autoHunt", true);
            int fields = 0;
            try { fields = Agriculture.EnsureFields(w.map, false); } catch { }
            string msg = "Продовольствие: " + U.F(w.foodDays) + "/" + U.F(w.foodTargetDays) + " дн., с урожаем "
                + U.F(w.foodDaysWithCrops) + ", тренд " + U.Sign(w.foodTrend) + "/дн., грядки " + w.farmCells + "/"
                + Agriculture.Last.target + " кл. (блайт " + w.farmBlighted + ", добавил " + fields
                + "). Грядки и готовка в приоритете.";
            if (w.foodTrend < -0.2f) msg += " Запас падает — расширяю посевы и запрещаю продажу еды.";
            return SkillResult.Yes(msg, MetricKind.Food, 30000);
        }

        private static SkillResult HoldFood(ColonyWorld w)
        {
            Policy.HoldFood = true;
            return SkillResult.Yes("Еды " + U.F(w.foodDays) + " дн. — продажу продовольствия останавливаю до восстановления запаса.",
                MetricKind.Food, 30000);
        }

        private static SkillResult Kitchen(ColonyWorld w)
        {
            bool placed = EnsureBuilding(w, new[] { "ElectricStove", "FueledStove" }, true, "кухонную плиту", 2);
            if (!placed) placed = EnsureBuilding(w, new[] { "TableButcher" }, true, "разделочный стол", 1);
            return placed
                ? SkillResult.Yes("Кухня: заложил недостающее место для готовки/разделки, чтобы еда не простаивала.", MetricKind.Food, 60000)
                : SkillResult.Yes("Кухня в порядке: проверяю, что готовка не простаивает.", MetricKind.Food, 60000);
        }

        private static SkillResult Warmth(ColonyWorld w)
        {
            Crisis.Run(w.map);
            string msg = "Температура " + w.outdoorTemp.ToString("0") + "°C";
            if (w.hypothermia > 0) msg += ", переохлаждение у " + w.hypothermia;
            if (w.heatstroke > 0) msg += ", перегрев у " + w.heatstroke;
            msg += w.outdoorTemp < 0f ? ": костёр и тёплая одежда, работа в помещениях." : ": укрытие и работа в тени.";
            return SkillResult.Yes(msg, MetricKind.Health, 20000);
        }

        private static SkillResult NeedsSkill(ColonyWorld w)
        {
            Needs.Run(w.map);
            return SkillResult.Yes("Потребности: настроение " + U.Pct(w.moodAvg) + ", отдых " + U.Pct(w.restMin)
                + ". Отдых/радость/гигиена по показаниям.", MetricKind.Mood, 20000);
        }

        private static SkillResult RestSkill(ColonyWorld w)
        {
            int rested = 0;
            foreach (PawnProfile p in w.profiles)
            {
                if (p.pawn == null || p.pawn.timetable == null || p.downed) continue;
                if (p.rest > 0.3f) continue;
                if (w.threat) continue;
                int hour = GenLocalDate.HourOfDay(p.pawn);
                for (int i = 0; i < 4; i++) p.pawn.timetable.SetAssignment((hour + i) % 24, TimeAssignmentDefOf.Sleep);
                rested++;
            }
            BotBridge.SetBotSetting("autoTimetable", true);
            return SkillResult.Yes("Отдых: минимум " + U.Pct(w.restMin) + ", вымотаны " + w.exhausted
                + (rested > 0 ? ". Отправил спать: " + rested + "." : "."), MetricKind.Mood, 12000);
        }

        private static SkillResult PowerSkill(ColonyWorld w)
        {
            PowerManager.Run(w.map);
            string msg = "Энергия: " + (int)w.powerCapacity + " Вт при потреблении " + (int)w.powerDemand
                + " Вт (дефицит " + (int)w.powerDeficit + "). Один чертёж за раз, лишние генераторы не спамлю.";
            return SkillResult.Yes(msg, MetricKind.Power, 30000);
        }

        private static SkillResult Housing(ColonyWorld w)
        {
            int placed = 0;
            if (w.beds < w.pop && EnsureBed(w)) placed++;
            if (w.pop >= 2) EnsureBuilding(w, new[] { "SimpleResearchBench", "HiTechResearchBench" }, true, "верстак исследований", 1);
            string msg = placed > 0
                ? "Жильё: кроватей " + w.beds + " на " + w.pop + " — заложил ещё одну."
                : "Жильё: кроватей " + w.beds + " на " + w.pop + " — проверяю базовые постройки.";
            return SkillResult.Yes(msg, MetricKind.Housing, 60000);
        }

        private static SkillResult CaveBasePattern(ColonyWorld w)
        {
            string msg = Patterns.CavePattern(w.map);
            return SkillResult.Yes(msg, MetricKind.Defense, 60000);
        }

        private static SkillResult FireSafetyPattern(ColonyWorld w)
        {
            bool ok = Patterns.FireSafety(w.map);
            return ok
                ? SkillResult.Yes("Пожарная безопасность: поставил пенную ловушку у батарей.", MetricKind.Housing, 90000)
                : SkillResult.No("Пожарная безопасность: батареи есть, но подходящее место или материал пока не найдены.");
        }

        private static SkillResult RoofClear(ColonyWorld w)
        {
            int n = Roofs.MarkProblemRoofs(w.map);
            return n > 0
                ? SkillResult.Yes("Крыша: снимаю её с " + n + " клеток над панелями/грядками — свет важнее.", MetricKind.Housing, 60000)
                : SkillResult.No("Крыша: мешающих участков не нашёл.");
        }

        private static SkillResult ZoneCleanup(ColonyWorld w)
        {
            int n = Zones.Cleanup(w.map);
            return n > 0
                ? SkillResult.Yes("Зоны: убрал " + n + " пустых/дублирующихся зон.", MetricKind.Housing, 60000)
                : SkillResult.No("Зоны: устаревших зон нет.");
        }

        private static SkillResult Clean(ColonyWorld w)
        {
            BotBridge.SetBotSetting("autoCleanHomeArea", true);
            return SkillResult.Yes("Уборка включена: грязь бьёт по настроению и медицине.", MetricKind.Mood, 30000);
        }

        private static SkillResult Produce(ColonyWorld w)
        {
            Planner.Run(w.map);
            return SkillResult.Yes("Производство: пересчитал прибыльность рецептов и задания на верстаках.", MetricKind.Wealth, 30000);
        }

        private static SkillResult Build(ColonyWorld w)
        {
            Infrastructure.Run(w.map);
            return SkillResult.Yes("Стройка для торговли: консоль, маяк, прибыльные верстаки — по одному шагу.", MetricKind.Wealth, 60000);
        }

        private static SkillResult TradePolicy(ColonyWorld w)
        {
            var s = TradeEmpireMod.S;
            if (s == null) return SkillResult.No("");
            if (!s.economyLearning) return SkillResult.No("Обучение торговле выключено — цены не трогаю.");
            var c = Ledger.C;
            int now = w.tick;
            string msg = "Торговая политика: ";
            float minSell = s.minSellRatio;
            if (c != null && c.deals == 0 && w.silver > s.silverReserve && w.day > 3)
            {
                minSell = Mathf.Max(0.12f, minSell - 0.015f);
                msg += "сделок ещё нет — снижаю минимальную цену продажи. ";
            }
            else if (c != null && c.lastDealTick > 0 && now - c.lastDealTick < 60000)
            {
                minSell = Mathf.Min(0.5f, minSell + 0.005f);
                msg += "торговля идёт — чуть поднимаю планку цены. ";
            }
            if (w.medicine < 8 || w.steel < 150) s.maxBuyRatio = Mathf.Min(3.2f, s.maxBuyRatio + 0.02f);
            else s.maxBuyRatio = U.MoveTo(s.maxBuyRatio, 2.1f, 0.01f);
            if (w.silver < s.silverReserve) s.stockTargetValue = Mathf.Max(800, s.stockTargetValue - 100);
            else if (w.wealthTrend > 150f) s.stockTargetValue = Mathf.Min(9000, s.stockTargetValue + 100);
            s.minSellRatio = minSell;
            msg += "продажа от " + U.F(minSell, 2) + " рыночной, закупка до " + U.F(s.maxBuyRatio, 2) + ".";
            return SkillResult.Yes(msg, MetricKind.Wealth, 60000);
        }

        private static SkillResult Recruit(ColonyWorld w)
        {
            int target = Math.Max(6, T.BotInt("targetColonists", 10));
            BotBridge.SetBotSetting("autoReleasePrisoners", false);     // пленных лучше вербовать, чем терять
            if (w.pop < target) BotBridge.SetBotSetting("autoJoy", true);
            return SkillResult.Yes("Рост: " + w.pop + " из " + target + " человек. Пришлых принимаю, если еда и настроение позволяют; пленных вербую.",
                MetricKind.Population, 60000);
        }

        private static SkillResult Prisoners(ColonyWorld w)
        {
            Pawn best = null; float bestScore = -1f;
            foreach (Pawn p in w.colonists)
            {
                if (p.workSettings == null || p.skills == null) continue;
                WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Warden");
                if (wt == null || p.WorkTypeIsDisabled(wt)) continue;
                float sc = p.skills.AverageOfRelevantSkillsFor(wt);
                if (sc > bestScore) { bestScore = sc; best = p; }
            }
            if (best != null)
            {
                WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Warden");
                if (wt != null) best.workSettings.SetPriority(wt, 1);
            }
            return SkillResult.Yes("Пленных " + w.prisoners + ": назначил лучшего переговорщика на вербовку.", MetricKind.Population, 30000);
        }

        private static SkillResult IdeologySkill(ColonyWorld w)
        {
            Needs.Run(w.map);
            return SkillResult.Yes("Идеология: проверяю здания заветов и общее настроение.", MetricKind.Mood, 60000);
        }

        private static SkillResult Animals(ColonyWorld w)
        {
            if (Vanilla.Active) Vanilla.Tame(w.map);
            BotBridge.SetBotSetting("autoTame", true);
            return SkillResult.Yes("Животные: приручение включено — еда, ткани и помощь в бою.", MetricKind.Food, 90000);
        }

        // ------------------------------------------------------------------ помощники
        /// <summary>Кровати нужны штуками: «уже есть одна» не повод не строить вторую.</summary>
        private static bool EnsureBed(ColonyWorld w)
        {
            try
            {
                int pendingBeds = w.map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                    .Count(t =>
                    {
                        if (t.Faction != Faction.OfPlayer) return false;
                        ThingDef bd = t.def.entityDefToBuild as ThingDef;
                        return bd != null && bd.thingClass != null && typeof(Building_Bed).IsAssignableFrom(bd.thingClass);
                    });
                if (pendingBeds >= 2) return false;
                foreach (string name in new[] { "Bed", "DoubleBed", "HospitalBed" })
                {
                    ThingDef def = T.Def(name);
                    if (def == null || !Infrastructure.Available(def)) continue;
                    if (Infrastructure.PlaceNear(w.map, def, Infrastructure.Anchor(w.map), 3, 24, true))
                    {
                        Mind.Think("быт", "Заложил кровать («" + def.label + "»): " + w.beds + " на " + w.pop + " человек.");
                        return true;
                    }
                }
            }
            catch (Exception e) { T.Warn("Кровать не заложена: " + e.Message); }
            return false;
        }

        private static bool EnsureBuilding(ColonyWorld w, string[] names, bool indoors, string what, int maxPending)
        {
            try
            {
                int pendingBlueprints = w.map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer);
                if (pendingBlueprints >= maxPending + 2) return false;
                foreach (string name in names)
                {
                    ThingDef def = T.Def(name);
                    if (def == null || !Infrastructure.Available(def)) continue;
                    if (Infrastructure.Exists(w.map, def)) continue;
                    if (Infrastructure.PlaceNear(w.map, def, Infrastructure.Anchor(w.map), 3, 24, indoors))
                    {
                        Mind.Think("быт", "Заложил " + what + " («" + def.label + "»).");
                        return true;
                    }
                }
            }
            catch (Exception e) { T.Warn("Постройка «" + what + "» не удалась: " + e.Message); }
            return false;
        }

        /// <summary>Новая игра: таймеры и очередь проверок сбрасываются, знания остаются.</summary>
        public static void ResetRuntime()
        {
            lastUse.Clear();
            pending.Clear();
            breakers.Clear();
        }

        public static string StatusLine(ColonyWorld w)
        {
            if (w == null) return "Навыки: ожидание карты.";
            int total = Knowledge.Skills.Values.Sum(s => s.n);
            return "Навыки: " + all.Count + " в библиотеке, исходов " + total + ", в очереди проверок " + pending.Count + ".";
        }
    }

    /// <summary>
    /// Советник по исследованиям: оценивает, что реально даст колонии больше пользы сейчас.
    /// Сам ставит проект и сторожит, чтобы наука не встала.
    /// </summary>
    public static class ResearchAdvisor
    {
        private static int lastSetDay = -99;
        private static int stalledSince = -1;
        private static string lastProject = "";
        private static bool botDisabled;
        private static bool failureLogged;

        public static SkillResult Step(ColonyWorld w)
        {
            var s = TradeEmpireMod.S;
            if (s == null || !s.smartResearch || !s.deepBrain)
                return SkillResult.No("");

            DefExpert.ResearchPick best = DefExpert.BestResearch(w);
            if (best.project == null)
                return SkillResult.No("");

            ResearchProjectDef current = BotBridge.CurrentResearch();
            float currentScore = current != null ? DefExpert.ResearchScoreOf(current, w) : 0f;
            bool needSwitch = current == null || current.IsFinished || best.score > currentScore * 1.35f;

            if (needSwitch)
            {
                bool ok = BotBridge.SetResearch(best.project);
                if (!ok)
                {
                    if (!failureLogged) { failureLogged = true; T.Warn("Не удалось поставить исследование напрямую — оставляю встроенный модуль бота."); }
                    return SkillResult.No("Не удалось переключить проект — не рискую и оставляю науку боту.");
                }
                if (TradeEmpireMod.S != null && TradeEmpireMod.S.adaptiveTakeover)
                {
                    BotBridge.SetBotSetting("autoResearch", false);
                    botDisabled = true;
                }
                else botDisabled = false;
                lastSetDay = w.day;
                stalledSince = -1;
                if (lastProject != best.project.defName)
                {
                    lastProject = best.project.defName;
                    return SkillResult.Yes("Исследование: выбрал «" + best.project.LabelCap + "» — " + best.why
                        + ". Прежний проект: " + (current != null ? current.LabelCap.ToString() : "не было") + ".", MetricKind.Research, 60000);
                }
                return SkillResult.Yes("", MetricKind.Research, 60000);
            }

            if (current != null) stalledSince = -1;
            return SkillResult.No("");
        }

        public static SkillResult Guard(ColonyWorld w)
        {
            var s = TradeEmpireMod.S;
            if (s == null || !s.smartResearch) return SkillResult.No("");
            ResearchProjectDef current = BotBridge.CurrentResearch();
            if (current != null) { stalledSince = -1; return SkillResult.No(""); }
            if (stalledSince < 0) stalledSince = w.tick;
            if (w.tick - stalledSince > 60000)
            {
                BotBridge.SetBotSetting("autoResearch", true);
                stalledSince = -1;
                botDisabled = false;
                return SkillResult.Yes("Наука простаивала больше дня — вернул управление встроенному модулю бота, чтобы не потерять прогресс.",
                    MetricKind.Research, 60000);
            }
            return SkillResult.No("");
        }

        public static void ResetRuntime()
        {
            lastSetDay = -99;
            stalledSince = -1;
            lastProject = "";
            failureLogged = false;
            botDisabled = false;
        }

        public static string Status(ColonyWorld w)
        {
            if (w == null) return "Наука: —";
            if (TradeEmpireMod.S == null || !TradeEmpireMod.S.smartResearch) return "Наука: встроенный модуль бота";
            ResearchProjectDef cur = BotBridge.CurrentResearch();
            return "Наука: " + (cur != null ? cur.LabelCap.ToString() : "проект не выбран") + ", доступно " + w.researchAvailable
                + ", завершено " + w.researchDone + (botDisabled ? " (управляю сам)" : "");
        }
    }
}
