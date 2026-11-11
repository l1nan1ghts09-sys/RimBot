using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Командный центр бота: что важно, что делает, чему научился и почему.</summary>
    public class BrainWindow : Window
    {
        private static readonly string[] Tabs = { "Обзор", "Цели", "План", "Обучение", "Знания", "Журнал" };
        private static readonly string[] Cats = { "", "цель", "обучение", "решение", "действие", "событие", "потеря", "оборона", "еда", "медицина", "наука", "экономика", "настроение", "работа", "энергия", "зависание", "торговля", "быт", "рост", "итог" };

        private int tab;
        private readonly Vector2[] scroll = new Vector2[Tabs.Length];
        private string filter = "";
        private float lastKnowledgeRefresh;
        private readonly List<string> researchLines = new List<string>();
        private string foodHint = "—";
        private string medHint = "—";

        public BrainWindow()
        {
            draggable = true;
            resizeable = true;
            doCloseX = true;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            forcePause = false;
        }

        public override Vector2 InitialSize { get { return new Vector2(680f, 700f); } }

        public override void DoWindowContents(Rect inRect)
        {
            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 30f), "Мозг бота — командный центр");
            y += 32f;
            Text.Font = GameFont.Small;

            float tabW = (inRect.width - 10f) / Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                Rect r = new Rect(i * tabW, y, tabW - 2f, 26f);
                if (Widgets.ButtonText(r, (tab == i ? "» " : "") + Tabs[i])) tab = i;
            }
            y += 30f;

            Rect outRect = new Rect(0, y, inRect.width, inRect.height - y);
            Rect view = new Rect(0, 0, outRect.width - 20f, 2600f);
            Widgets.BeginScrollView(outRect, ref scroll[tab], view);
            float yy = 0f;
            switch (tab)
            {
                case 0: DrawOverview(view, ref yy); break;
                case 1: DrawGoals(view, ref yy); break;
                case 2: DrawPlan(view, ref yy); break;
                case 3: DrawLearning(view, ref yy); break;
                case 4: DrawKnowledge(view, ref yy); break;
                default: DrawLog(view, ref yy); break;
            }
            Widgets.EndScrollView();
        }

        // ------------------------------------------------------------------ обзор
        private void DrawOverview(Rect v, ref float y)
        {
            TradeSettings s = TradeEmpireMod.S;
            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;

            y += Label(v, y, "Состояние: " + (w != null ? w.Brief() : "карта ещё не загружена."), 0);
            y += 2f;
            if (brain != null) y += Label(v, y, brain.NowLine(), 0);
            y += 4f;

            if (w != null)
            {
                Head(v, ref y, "Главное сейчас");
                y += Label(v, y, Mind.Headline, 0);
                y += Label(v, y, Focus.Line, 0);
                y += Label(v, y, Reflect.CausesLine(w), 0);
                y += 4f;

                Head(v, ref y, "Заботы");
                foreach (Mind.Concern c in Mind.Concerns.Take(8))
                {
                    Rect bar = new Rect(0, y + 3f, 95f, 15f);
                    Widgets.DrawBox(bar);
                    GUI.color = Color.Lerp(Color.green, Color.red, U.Clamp01(c.severity));
                    GUI.DrawTexture(new Rect(bar.x + 1f, bar.y + 1f, Mathf.Max(0f, (bar.width - 2f) * U.Clamp01(c.severity)), bar.height - 2f), BaseContent.WhiteTex);
                    GUI.color = Color.white;
                    Widgets.Label(new Rect(102f, y, v.width - 102f, 20f), c.name + ": " + c.why + " → " + c.doing);
                    y += 21f;
                }
                y += 4f;

                Head(v, ref y, "Ключевые числа");
                y += Label(v, y, "Люди: " + w.pop + " (лежачих " + w.downed + ", срывов " + w.mental + ", свободны " + w.idle + "). "
                    + "Еда: " + U.F(w.foodDays) + " дн. (" + U.Sign(w.foodTrend) + "/дн.), с урожаем " + U.F(w.foodDaysWithCrops) + ".", 0);
                y += Label(v, y, "Угроза: " + (w.threat ? w.hostiles + " врагов, сила " + U.Pct(w.threatRatio) : "нет")
                    + ". Риск рейда " + U.Pct(w.baseRisk) + ". Оборона: турелей " + w.turrets + ", ловушек " + w.traps + ".", 0);
                y += Label(v, y, Defense.StatusShort, 0);
                y += Label(v, y, "Здоровье " + U.Pct(w.healthScore) + ", настроение " + U.Pct(w.moodAvg) + ", отдых " + U.Pct(w.restMin)
                    + ", лекарств " + w.medicine + ".", 0);
                y += Label(v, y, "Энергия " + (int)w.powerCapacity + "/" + (int)w.powerDemand + " Вт (дефицит " + (int)w.powerDeficit + "), "
                    + "батареи " + (int)w.powerStored + ".", 0);
                y += Label(v, y, "Богатство " + (int)w.wealth + " (" + U.Sign(w.wealthTrend) + "/дн.), верстаков " + w.tables
                    + ", заданий " + w.bills + ", строек " + (w.blueprints + w.frames) + ".", 0);
                y += Label(v, y, "Наука: завершено " + w.researchDone + ", доступно " + w.researchAvailable + ". " + ResearchAdvisor.Status(w), 0);
                y += 4f;

                Head(v, ref y, "Обучение");
                y += Label(v, y, brain != null ? brain.LearningLine() : Learning.Summary(), 0);
                y += Label(v, y, Ledger.Summary(), 0);
                y += 4f;
            }

            if (s != null)
            {
                Head(v, ref y, "Переключатели");
                float half = v.width / 2f - 4f;
                Widgets.CheckboxLabeled(new Rect(0, y, half, 24f), "Глубокий мозг", ref s.deepBrain);
                Widgets.CheckboxLabeled(new Rect(half + 8f, y, half, 24f), "Самообучение", ref s.learning); y += 26f;
                Widgets.CheckboxLabeled(new Rect(0, y, half, 24f), "Эксперименты", ref s.experiments);
                Widgets.CheckboxLabeled(new Rect(half + 8f, y, half, 24f), "Умные исследования", ref s.smartResearch); y += 26f;
                Widgets.CheckboxLabeled(new Rect(0, y, half, 24f), "Управлять скоростью", ref s.autoSpeed);
                Widgets.CheckboxLabeled(new Rect(half + 8f, y, half, 24f), "Не держать паузу", ref s.keepRunning); y += 26f;
                Widgets.CheckboxLabeled(new Rect(0, y, half, 24f), "Умные энергетика/работы", ref s.takeover);
                Widgets.CheckboxLabeled(new Rect(half + 8f, y, half, 24f), "Сторож зависаний", ref s.watchdog); y += 30f;

            }

            y += Label(v, y, "Подсказка: Ctrl+T открывает и закрывает эту панель; кнопка «Мозг бота» — справа внизу.", 0);
        }

        // ------------------------------------------------------------------ цели
        private void DrawGoals(Rect v, ref float y)
        {
            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;
            if (w == null) { y += Label(v, y, "Жду карту.", 0); return; }

            y += Label(v, y, "Цели ранжируются по срочности, веса учатся по прошлым исходам, уроки из дневника добавляют поправку. "
                + "Текущий контекст: " + Learning.CtxText(w.ctx) + ".", 0);
            y += 4f;

            List<ColonyGoal> goals = brain != null && brain.GoalList.Count > 0 ? brain.GoalList : Goals.Evaluate(w);
            foreach (ColonyGoal g in goals.Take(14))
            {
                Rect bar = new Rect(0, y + 2f, 100f, 16f);
                Widgets.DrawBox(bar);
                GUI.color = Color.Lerp(Color.green, Color.red, g.utility);
                GUI.DrawTexture(new Rect(bar.x + 1f, bar.y + 1f, Mathf.Max(0f, (bar.width - 2f) * g.utility), bar.height - 2f), BaseContent.WhiteTex);
                GUI.color = Color.white;

                string mark = g.critical ? "!" : g.utility > 0.5f ? "•" : " ";
                Widgets.Label(new Rect(106f, y, v.width - 106f, 22f), mark + " " + g.name + " — " + U.Pct(g.utility)
                    + " (вес " + U.F(g.weight, 2) + ")");
                y += 21f;
                y += Label(v, y, "     " + g.why, 18);
                y += Label(v, y, "     → " + g.action, 18);
            }

            y += 6f;
            Head(v, ref y, "Чему бот научился по целям");
            int shown = 0;
            foreach (ColonyGoal g in goals)
            {
                if (shown++ >= 8) break;
                float learned = Knowledge.GoalWeight(g.id, w.goalCtx, 1f);
                float bias = Knowledge.LessonBias(g.id, w.ctx);
                if (Math.Abs(learned - 1f) < 0.03f && Math.Abs(bias) < 0.03f) continue;
                y += Label(v, y, g.name + ": вес " + U.F(learned, 2) + " (по опыту), поправка уроков " + U.Sign(bias) + ".", 0);
            }
        }

        // ------------------------------------------------------------------ план
        private void DrawPlan(Rect v, ref float y)
        {
            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;
            if (brain == null || w == null) { y += Label(v, y, "Жду карту.", 0); return; }

            Head(v, ref y, "Текущая цель");
            if (brain.Current != null)
            {
                y += Label(v, y, "«" + brain.Current.name + "» — " + brain.Current.why, 0);
                y += Label(v, y, "Действие: " + brain.Current.action, 0);
                y += Label(v, y, "Уверенность: " + U.Pct(brain.Current.utility) + ", вес " + U.F(brain.Current.weight, 2)
                    + ", критично: " + (brain.Current.critical ? "да" : "нет") + ".", 0);
            }
            else y += Label(v, y, "Цель ещё не выбрана.", 0);
            y += 6f;

            Head(v, ref y, "Фокус и порядок задач");
            y += Label(v, y, Focus.Line + ". Сейчас бот ведёт одну главную задачу, остальные — только по необходимости.", 0);
            y += Label(v, y, "Взвешенных решений: " + Utility.Recent.Count + ", принято " + Utility.AcceptedCount
                + ". Перед действием бот считает пользу, цену, риск и срочность.", 0);
            y += 4f;

            Head(v, ref y, "Последние решения");
            y += Label(v, y, Utility.Summary(8), 0);
            y += 4f;

            Head(v, ref y, "Место базы и ресурсы");
            y += Label(v, y, Site.Line(w.map), 0);
            y += Label(v, y, Resources.BudgetLine(w.map), 0);
            y += Label(v, y, BuildValue.Line(w.map), 0);
            y += 4f;

            Head(v, ref y, "Хранение, порча и механики");
            y += Label(v, y, FoodSafety.Line(w.map), 0);
            y += Label(v, y, Deterioration.Line(w.map), 0);
            y += Label(v, y, Storage.StatusLine(w.map), 0);
            y += Label(v, y, Mechanics.Line(w.map), 0);
            y += Label(v, y, AutoClose.Line(), 0);
            y += 4f;

            Head(v, ref y, "Оборона базы");
            y += Label(v, y, Defense.StatusDetail, 0);
            Defense.Report dr = Defense.Last;
            if (dr != null)
            {
                y += Label(v, y, "Тактика: " + dr.tactic + ". Укрытий " + dr.cover + ", стен " + dr.walls
                    + ", турелей " + dr.turrets + ", ловушек " + dr.traps + ", крытых клеток " + dr.roofed
                    + ", бойцов " + dr.fighters + " (стрелков " + dr.gunners + "), лекарств " + dr.medicine + ".", 0);
            }
            y += 6f;

            Head(v, ref y, "План обучения на сегодня");
            y += Label(v, y, brain.PlanSummary, 0);
            y += 4f;
            foreach (KnobDecision d in brain.TodayDecisions)
            {
                y += Label(v, y, (d.probe ? "ЭКСПЕРИМЕНТ: " : "ИЗМЕНЕНО: ") + d.knob.title + " = " + d.knob.ArmText(d.arm)
                    + ". " + (d.why.Length > 0 ? d.why : ""), 16);
            }
            y += 4f;

            Probe p = brain.ActiveProbe;
            if (p != null)
            {
                Knob k = KnobBank.Find(p.field);
                float remain = Mathf.Max(0f, (p.startTick + Mathf.Max(6000, k != null ? k.window - 6000 : 30000) - w.tick) / 60000f);
                y += Label(v, y, "Идёт проверка: " + (k != null ? k.title : p.field) + " = " + (k != null ? k.ArmText(p.arm) : p.arm.ToString())
                    + ", контекст " + Learning.CtxText(p.ctx) + ", осталось ~" + U.F(remain) + " дн. " + p.why, 0);
            }
            else y += Label(v, y, "Проверок в очереди нет: бот использует лучшие известные настройки.", 0);
            y += 6f;

            Head(v, ref y, "Надёжность навыков (по фактам)");
            List<KeyValuePair<string, LearnedSkill>> rows = new List<KeyValuePair<string, LearnedSkill>>();
            foreach (var kv in Knowledge.Skills)
                if (kv.Key.EndsWith("|*", StringComparison.Ordinal)) rows.Add(kv);
            foreach (var kv in rows.OrderByDescending(x => x.Value.n).Take(12))
            {
                string name = kv.Key.Substring(0, kv.Key.Length - 2);
                y += Label(v, y, name + ": успех " + U.Pct(kv.Value.Rate) + " из " + kv.Value.n
                    + " попыток, средний эффект " + U.Sign(kv.Value.Gain), 0);
            }
            if (rows.Count == 0) y += Label(v, y, "Пока нет завершённых проверок навыков — бот набирает опыт.", 0);
        }

        // ------------------------------------------------------------------ обучение
        private void DrawLearning(Rect v, ref float y)
        {
            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;
            if (w == null) { y += Label(v, y, "Жду карту.", 0); return; }

            y += Label(v, y, Learning.Summary(), 0);
            y += Label(v, y, "Ожидаемый интервал рейдов: " + U.F(Knowledge.ExpectedRaidInterval) + " дн. (из " + Knowledge.Raids.count
                + " наблюдений), последний рейд: день " + (Knowledge.LastRaidDay < 0 ? "—" : Knowledge.LastRaidDay.ToString()) + ".", 0);
            y += 4f;

            Head(v, ref y, "Оценка колонии по дням");
            float graphW = Mathf.Min(v.width - 4f, 520f);
            Rect graph = new Rect(0, y, graphW, 54f);
            Widgets.DrawBox(graph);
            List<int> scores = brain.DailyScores;
            if (scores.Count > 1)
            {
                float bw = Mathf.Max(2f, (graph.width - 4f) / Mathf.Min(60, scores.Count));
                int start = Mathf.Max(0, scores.Count - 60);
                for (int i = start; i < scores.Count; i++)
                {
                    float val = U.Clamp01(scores[i] / 1000f);
                    float h = Mathf.Max(2f, (graph.height - 4f) * val);
                    GUI.color = Color.Lerp(Color.red, Color.green, val);
                    GUI.DrawTexture(new Rect(graph.x + 2f + (i - start) * bw, graph.y + graph.height - 2f - h, bw - 1f, h), BaseContent.WhiteTex);
                }
                GUI.color = Color.white;
            }
            y += graph.height + 6f;

            Head(v, ref y, "Что в колонии важнее всего (мета-веса)");
            string[] names = { "еда", "здоровье", "настроение", "развитие/богатство", "безопасность", "наука" };
            for (int i = 0; i < names.Length && i < Knowledge.Meta.Length; i++)
            {
                float m = Knowledge.Meta[i];
                y += Label(v, y, names[i] + ": " + U.F(m, 2) + " (" + (m > 1.2f ? "важнее обычного" : m < 0.7f ? "менее важно" : "обычно") + ")", 0);
            }
            y += Label(v, y, "Наблюдений для мета-обучения: " + Knowledge.MetaN + ".", 0);
            y += 4f;

            Head(v, ref y, "Ручки: что лучше в текущих условиях");
            foreach (Knob k in KnobBank.All.Take(12))
            {
                int cur;
                if (!brain.ArmIndices.TryGetValue(k.field, out cur)) cur = k.prior;
                int best = cur;
                double bestMean = double.MinValue;
                int samples = 0;
                for (int i = 0; i < k.arms.Length; i++)
                {
                    int n = Knowledge.ArmSamples(k.field, i, w.ctx);
                    samples += n;
                    double m = Knowledge.ArmMean(k.field, i, w.ctx, 0.5);
                    if (m > bestMean) { bestMean = m; best = i; }
                }
                y += Label(v, y, k.title + ": сейчас «" + k.ArmText(cur) + "», лучший «" + k.ArmText(best) + "» (опытов "
                    + samples + ", оценка " + U.F((float)bestMean, 2) + ").", 0);
            }
            y += 4f;

            Head(v, ref y, "Уроки");
            if (Knowledge.Lessons.Count == 0) y += Label(v, y, "Уроков пока нет: бот набирает опыт и разбирает события.", 0);
            foreach (Lesson l in Knowledge.Lessons.OrderByDescending(x => x.conf).Take(10))
                y += Label(v, y, "[" + U.Pct(l.conf) + "] " + l.text, 16);
        }

        // ------------------------------------------------------------------ знания
        private void DrawKnowledge(Rect v, ref float y)
        {
            BrainComponent brain = BrainComponent.Get();
            ColonyWorld w = brain != null ? brain.World : null;
            if (w == null) { y += Label(v, y, "Жду карту.", 0); return; }
            RefreshKnowledge(w);

            y += Label(v, y, "Этап: " + (w.stage == Stage.Early ? "ранний" : w.stage == Stage.Mid ? "середина" : "поздний")
                + ", биом: " + w.biome + ", богатство: " + w.wealthTier + ", день " + w.day + ".", 0);
            y += Label(v, y, "Погода: " + w.weather + ", температура " + w.outdoorTemp.ToString("0") + "°C. "
                + (w.conditions.Count > 0 ? "События: " + string.Join(", ", w.conditions.ToArray()) + "." : "Активных событий нет."), 0);
            y += 4f;

            Head(v, ref y, "Питание и лекарства");
            y += Label(v, y, "Самая выгодная еда по питательности на серебро: " + foodHint + ".", 0);
            y += Label(v, y, "Лучшее лекарство: " + medHint + ". Запас: " + w.medicine + " (сила " + w.medicinePotency + ").", 0);
            y += Line(v, y, "Еда в колонии: " + U.F(w.foodNutrition) + " питания, растёт на грядках ещё " + U.F(w.growingNutrition)
                + ". Цель — " + U.F(w.foodTargetDays) + " дн.");
            y += 4f;

            Head(v, ref y, "Наука: что выгоднее сейчас");
            for (int i = 0; i < researchLines.Count; i++) y += Label(v, y, researchLines[i], 16);
            if (researchLines.Count == 0) y += Label(v, y, "Нет доступных проектов или наука не подключена.", 0);
            y += 4f;

            Head(v, ref y, "Оборона и экономика");
            y += Line(v, y, "Оборона: врагов " + w.hostiles + ", наша сила " + U.Pct(w.threatRatio) + " от врага, турелей " + w.turrets
                + ", ловушек " + w.traps + ". Ожидаемый рейд через ~" + U.F(Mathf.Max(0f, Knowledge.ExpectedRaidInterval - w.daysSinceThreat)) + " дн.");
            y += Line(v, y, "Экономика: " + Ledger.Summary());
            y += Line(v, y, "Склад: серебро " + w.silver + ", сталь " + w.steel + ", компоненты " + w.components + ", дерево " + w.wood
                + ", ткань " + w.cloth + ". Кроватей " + w.beds + " на " + w.pop + ". Грядок " + w.growingZones + ".");
            y += 4f;

            Head(v, ref y, "Лучшие известные уроки");
            foreach (Lesson l in Knowledge.Lessons.OrderByDescending(x => x.conf).Take(6))
                y += Label(v, y, "• " + l.text, 16);
        }

        private void RefreshKnowledge(ColonyWorld w)
        {
            if (Time.realtimeSinceStartup - lastKnowledgeRefresh < 2f) return;
            lastKnowledgeRefresh = Time.realtimeSinceStartup;
            foodHint = DefExpert.BestFoodHint();
            medHint = DefExpert.BestMedicineHint();
            researchLines.Clear();
            try
            {
                List<DefExpert.ResearchPick> picks = new List<DefExpert.ResearchPick>();
                ResearchProjectDef current = BotBridge.CurrentResearch();
                foreach (ResearchProjectDef p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                {
                    if (!ColonyWorld.CanStart(p)) continue;
                    string why;
                    float sc = DefExpert.ResearchScore(p, w, out why);
                    if (current != null && p == current) sc *= 1.25f;
                    picks.Add(new DefExpert.ResearchPick { project = p, score = sc, why = why });
                }
                foreach (DefExpert.ResearchPick pick in picks.OrderByDescending(x => x.score).Take(6))
                    researchLines.Add((pick.project == current ? "→ " : "  ") + pick.why);
            }
            catch { }
        }

        // ------------------------------------------------------------------ журнал
        private void DrawLog(Rect v, ref float y)
        {
            if (Widgets.ButtonText(new Rect(0, y, 200f, 24f), "Фильтр: " + (filter == "" ? "все" : filter)))
            {
                int i = Array.IndexOf(Cats, filter);
                filter = Cats[(i + 1) % Cats.Length];
                scroll[tab] = Vector2.zero;
            }
            y += 28f;
            y += Label(v, y, "Показаны последние решения, проверки и уроки. Категория: " + (filter == "" ? "все" : filter) + ".", 0);
            y += 4f;

            List<Mind.Thought> items = Mind.Log.Where(t => filter == "" || t.cat == filter).Reverse().Take(160).ToList();
            foreach (Mind.Thought t in items)
            {
                string head = "[д" + (t.tick / 60000) + " " + ((t.tick % 60000) / 2500) + "ч] " + t.cat + ": ";
                y += Label(v, y, head + t.text, 14);
            }
            if (items.Count == 0) y += Label(v, y, "Пока пусто.", 0);
        }

        // ------------------------------------------------------------------ рисование
        private static void Head(Rect v, ref float y, string text)
        {
            y += 4f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, v.width, 24f), text);
            Text.Font = GameFont.Small;
            y += 26f;
        }

        private static int Label(Rect v, float y, string text, int indent)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float x = indent;
            float width = Mathf.Max(60f, v.width - x);
            float h = Mathf.Max(18f, Text.CalcHeight(text, width));
            Widgets.Label(new Rect(x, y, width, h), text);
            return (int)h + 2;
        }

        private static int Line(Rect v, float y, string text)
        {
            float h = Mathf.Max(18f, Text.CalcHeight(text, v.width));
            Widgets.Label(new Rect(0, y, v.width, h), text);
            return (int)h + 2;
        }
    }
}
