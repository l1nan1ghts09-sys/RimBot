using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Настройки надстройки (Options -> Mod settings -> Торговая империя).</summary>
    public class TradeSettings : ModSettings
    {
        public bool enabled = true;
        public bool orbitalTrade = true;      // торговля с орбитальными кораблями через консоль связи
        public bool visitorTrade = true;      // авто-закрытие/исполнение окон торговли (караваны-гости)
        public bool production = true;        // авто-bills по плотности прибыли
        public bool infrastructure = true;    // консоль связи, маяк, верстаки
        public bool researchBoost = true;     // торговые технологии вперёд
        public bool brain = true;             // телеметрия, антикризисные реакции, дневник
        public bool learning = true;          // самообучение параметров бота
        public bool keepRunning = true;       // снимать паузу, если игра стоит >6 сек без окон
        public bool showHud = true;           // кнопка «Мысли бота» (Ctrl+T)
        public bool autoControl = true;       // авто-выбор в диалогах событий, чистка писем
        public bool quests = true;            // принимать безопасные квесты
        public bool autoSpeed = true;         // бот сам управляет скоростью времени
        public bool takeover = true;          // заменить энергетику и приоритеты работ бота своими
        public bool watchdog = true;          // разрыв зацикливаний
        public bool vanillaAuto = true;       // автономный режим без основного бота
        public bool autoStorage = true;       // склады, настройки хранения и проверка порчи
        public bool autoDevelopment = true;   // исследования и назначение специалистов для развития
        // ---- 2.0: когнитивное ядро ----
        public bool deepBrain = true;         // цели, планы, прогнозы, рефлексия
        public bool smartResearch = true;     // выбирать исследования по расчётной ценности
        public bool adaptiveTakeover = true;  // сам решает, какие подсистемы бота взять на себя
        public bool experiments = true;       // разрешить осторожные эксперименты с настройками
        public bool verboseWhy = true;        // подробные объяснения решений в панели
        public bool economyLearning = true;   // учиться на фактических сделках
        public float riskTolerance = 0.35f;   // 0 — осторожно, 1 — рискованно
        public int calmSpeed = 3;             // скорость в спокойствии: 1 обычная, 2 быстрая, 3 сверхбыстрая
        public bool fastSpeed = true;         // всегда сверхбыстрая в спокойствии, даже если ползунок старый
        public int runInterval = 600;         // тиков между проходами
        public int silverReserve = 400;       // не тратить ниже
        public int minDealValue = 200;        // минимальная сумма продаж для сделки
        public int stockTargetValue = 3000;   // сколько серебра товара держать на каждый рецепт
        public int recipesPerTable = 3;
        public int maxBillsPerTable = 8;
        public float minSellRatio = 0.30f;    // цена продажи / рыночная; ниже — не продаём
        public float maxBuyRatio = 2.2f;      // цена покупки / рыночная; выше — не покупаем
        public int minSteel = 300;            // не продавать сталь ниже этого запаса
        public int minComponents = 20;        // не продавать компоненты ниже запаса
        public int minMedicine = 12;          // не продавать лекарства ниже запаса
        public int minFoodDays = 12;          // целевой запас еды в днях для торговли
        public int maxDealSilver = 4000;      // максимум серебра на одну сделку (0 — без лимита)
        public float huntRiskLimit = 0.45f;   // макс. допустимый риск охоты (0..1)
        public int huntMaxTargets = 2;        // сколько животных помечать за раз
        public bool autoDefense = true;       // оценка обороны, укрытия, ловушки, стены, турели
        public bool defenseRoofs = true;      // крыша над ключевыми помещениями
        public int defenseTurretCap = 6;      // максимум турелей
        public int defenseTrapCap = 8;        // максимум ловушек
        public bool craftItems = true;        // крафт нужных вещей: оружие, одежда, лекарства, компоненты
        public bool focusTasks = true;        // одна главная задача за раз, без распыления
        public bool autoHousing = true;       // строить дом и заселять его
        public bool autoCloseWindows = true;  // закрывать только оповещения, которые ставят игру на паузу
        public bool autoCloseNotifications = true; // убирать письма и всплывающие сообщения
        public bool autoRoofs = true;          // снимать крышу над солнечными панелями/грядками
        public bool autoZoneCleanup = true;    // убирать пустые и дублирующиеся зоны
        public bool autoPatterns = true;       // пещерная база, пожарная безопасность и т.п.
        public bool autoFields = true;         // умный выбор участка под грядки, культура и блайт
        public bool autoSurvival = true;       // шаблоны выживания: голод, холод, жара, осадки и т.п.

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref orbitalTrade, "orbitalTrade", true);
            Scribe_Values.Look(ref visitorTrade, "visitorTrade", true);
            Scribe_Values.Look(ref production, "production", true);
            Scribe_Values.Look(ref infrastructure, "infrastructure", true);
            Scribe_Values.Look(ref researchBoost, "researchBoost", true);
            Scribe_Values.Look(ref brain, "brain", true);
            Scribe_Values.Look(ref learning, "learning", true);
            Scribe_Values.Look(ref keepRunning, "keepRunning", true);
            Scribe_Values.Look(ref showHud, "showHud", true);
            Scribe_Values.Look(ref autoControl, "autoControl", true);
            Scribe_Values.Look(ref quests, "quests", true);
            Scribe_Values.Look(ref autoSpeed, "autoSpeed", true);
            Scribe_Values.Look(ref calmSpeed, "calmSpeed", 3);
            Scribe_Values.Look(ref fastSpeed, "fastSpeed", true);
            Scribe_Values.Look(ref takeover, "takeover", true);
            Scribe_Values.Look(ref watchdog, "watchdog", true);
            Scribe_Values.Look(ref vanillaAuto, "vanillaAuto", true);
            Scribe_Values.Look(ref autoStorage, "autoStorage", true);
            Scribe_Values.Look(ref autoDevelopment, "autoDevelopment", true);
            Scribe_Values.Look(ref deepBrain, "deepBrain", true);
            Scribe_Values.Look(ref smartResearch, "smartResearch", true);
            Scribe_Values.Look(ref adaptiveTakeover, "adaptiveTakeover", true);
            Scribe_Values.Look(ref experiments, "experiments", true);
            Scribe_Values.Look(ref verboseWhy, "verboseWhy", true);
            Scribe_Values.Look(ref economyLearning, "economyLearning", true);
            Scribe_Values.Look(ref riskTolerance, "riskTolerance", 0.35f);
            Scribe_Values.Look(ref runInterval, "runInterval", 600);
            Scribe_Values.Look(ref silverReserve, "silverReserve", 400);
            Scribe_Values.Look(ref minDealValue, "minDealValue", 200);
            Scribe_Values.Look(ref stockTargetValue, "stockTargetValue", 3000);
            Scribe_Values.Look(ref recipesPerTable, "recipesPerTable", 3);
            Scribe_Values.Look(ref maxBillsPerTable, "maxBillsPerTable", 8);
            Scribe_Values.Look(ref minSellRatio, "minSellRatio", 0.30f);
            Scribe_Values.Look(ref maxBuyRatio, "maxBuyRatio", 2.2f);
            Scribe_Values.Look(ref minSteel, "minSteel", 300);
            Scribe_Values.Look(ref minComponents, "minComponents", 20);
            Scribe_Values.Look(ref minMedicine, "minMedicine", 12);
            Scribe_Values.Look(ref minFoodDays, "minFoodDays", 12);
            Scribe_Values.Look(ref maxDealSilver, "maxDealSilver", 4000);
            Scribe_Values.Look(ref huntRiskLimit, "huntRiskLimit", 0.45f);
            Scribe_Values.Look(ref huntMaxTargets, "huntMaxTargets", 2);
            Scribe_Values.Look(ref autoDefense, "autoDefense", true);
            Scribe_Values.Look(ref defenseRoofs, "defenseRoofs", true);
            Scribe_Values.Look(ref defenseTurretCap, "defenseTurretCap", 6);
            Scribe_Values.Look(ref defenseTrapCap, "defenseTrapCap", 8);
            Scribe_Values.Look(ref craftItems, "craftItems", true);
            Scribe_Values.Look(ref focusTasks, "focusTasks", true);
            Scribe_Values.Look(ref autoHousing, "autoHousing", true);
            Scribe_Values.Look(ref autoCloseWindows, "autoCloseWindows", true);
            Scribe_Values.Look(ref autoCloseNotifications, "autoCloseNotifications", true);
            Scribe_Values.Look(ref autoRoofs, "autoRoofs", true);
            Scribe_Values.Look(ref autoZoneCleanup, "autoZoneCleanup", true);
            Scribe_Values.Look(ref autoPatterns, "autoPatterns", true);
            Scribe_Values.Look(ref autoFields, "autoFields", true);
            Scribe_Values.Look(ref autoSurvival, "autoSurvival", true);
            base.ExposeData();
        }
    }

    public class TradeEmpireMod : Mod
    {
        public static TradeSettings S;
        private Vector2 scroll;

        public TradeEmpireMod(ModContentPack content) : base(content)
        {
            S = GetSettings<TradeSettings>();
            T.Info("RimWorldBot (vanilla) загружен: автономный автопилот колонии.");
            T.Safe("ResearchBoost", ResearchBoost.InjectPriorities);
        }

        public override string SettingsCategory() { return "RimWorldBot — автопилот колонии"; }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var view = new Rect(0, 0, inRect.width - 20f, 2050f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);
            l.Label("Мозг 2.0: восприятие → цели → навыки → проверка результата → обучение. Знания хранятся в Config/RimBotMind.txt.");
            l.GapLine();
            l.CheckboxLabeled("Включить автопилот", ref S.enabled);
            l.CheckboxLabeled("Орбитальная торговля (консоль связи)", ref S.orbitalTrade);
            l.CheckboxLabeled("Авто-сделки в окнах торговли (гости/караваны)", ref S.visitorTrade);
            l.CheckboxLabeled("Авто-производство по прибыльности", ref S.production);
            l.CheckboxLabeled("Авто-строительство (консоль, маяк, верстаки)", ref S.infrastructure);
            l.CheckboxLabeled("Приоритет торговых исследований", ref S.researchBoost);
            l.CheckboxLabeled("Мозг: кризисы, дневник, телеметрия", ref S.brain);
            l.CheckboxLabeled("Самообучение параметров (между играми)", ref S.learning);
            l.CheckboxLabeled("Не давать игре стоять на паузе (авто-запуск)", ref S.keepRunning);
            l.CheckboxLabeled("Кнопка «Мысли бота» на экране (Ctrl+T)", ref S.showHud);
            l.CheckboxLabeled("Авто-выбор в диалогах событий и чистка писем", ref S.autoControl);
            l.CheckboxLabeled("Принимать безопасные квесты", ref S.quests);
            l.CheckboxLabeled("Бот управляет скоростью времени", ref S.autoSpeed);
            l.CheckboxLabeled("Всегда сверхбыстрая в спокойствии", ref S.fastSpeed);
            l.CheckboxLabeled("Умная энергетика и приоритеты работ", ref S.takeover);
            l.CheckboxLabeled("Разрыв зацикливаний (сторож)", ref S.watchdog);
            l.CheckboxLabeled("Автономный режим: оборона, охота, посевы, добыча", ref S.vanillaAuto);
            l.CheckboxLabeled("Склады: общий, продуктовый и проверка порчи", ref S.autoStorage);
            l.CheckboxLabeled("Развитие: исследования и назначение специалистов", ref S.autoDevelopment);
            l.GapLine();
            l.Label("— Когнитивное ядро 2.0 —");
            l.CheckboxLabeled("Глубокий мозг: цели, планы, прогнозы, рефлексия", ref S.deepBrain);
            l.CheckboxLabeled("Сам выбирает исследования по ценности", ref S.smartResearch);
            l.CheckboxLabeled("Адаптивно брать подсистемы бота на себя", ref S.adaptiveTakeover);
            l.CheckboxLabeled("Осторожные эксперименты (самообучение)", ref S.experiments);
            l.CheckboxLabeled("Подробно объяснять решения", ref S.verboseWhy);
            l.CheckboxLabeled("Учиться на фактических сделках", ref S.economyLearning);
            l.Label("Склонность к риску: " + S.riskTolerance.ToString("0.00"));
            S.riskTolerance = l.Slider(S.riskTolerance, 0f, 1f);
            if (l.ButtonText("Сбросить накопленное обучение"))
            {
                Learning.ResetAll();
                T.Info("Обучение сброшено: статистика и уроки удалены, бот начнёт с чистого листа.");
            }
            l.GapLine();
            S.calmSpeed = Slider(l, "Скорость в спокойствии (1 обычная, 2 быстрая, 3 сверхбыстрая)", S.calmSpeed, 1, 3);
            l.GapLine();
            S.silverReserve = Slider(l, "Резерв серебра", S.silverReserve, 0, 5000);
            S.minDealValue = Slider(l, "Мин. сумма продаж для сделки", S.minDealValue, 50, 3000);
            S.stockTargetValue = Slider(l, "Целевой запас на рецепт (серебро)", S.stockTargetValue, 500, 15000);
            S.recipesPerTable = Slider(l, "Рецептов на верстак", S.recipesPerTable, 1, 8);
            S.maxBillsPerTable = Slider(l, "Макс. заданий на верстак", S.maxBillsPerTable, 3, 20);
            l.Label("Мин. цена продажи / рыночная: " + S.minSellRatio.ToString("0.00"));
            S.minSellRatio = l.Slider(S.minSellRatio, 0.05f, 1f);
            l.Label("Макс. цена покупки / рыночная: " + S.maxBuyRatio.ToString("0.00"));
            S.maxBuyRatio = l.Slider(S.maxBuyRatio, 1f, 4f);
            l.GapLine();
            S.minSteel = Slider(l, "Не продавать сталь ниже", S.minSteel, 0, 2000);
            S.minComponents = Slider(l, "Не продавать компоненты ниже", S.minComponents, 0, 100);
            S.minMedicine = Slider(l, "Не продавать лекарства ниже", S.minMedicine, 0, 50);
            S.minFoodDays = Slider(l, "Запас еды для торговли (дней)", S.minFoodDays, 4, 40);
            S.maxDealSilver = Slider(l, "Лимит серебра на сделку", S.maxDealSilver, 0, 20000);
            if (l.ButtonText("Закрыть/отменить текущую торговлю")) AutoTrader.CancelTrade();
            l.GapLine();
            l.Label("Охота с оценкой риска");
            l.Label("Макс. риск охоты: " + U.Pct(S.huntRiskLimit) + " (хищник, ответка, стая, расстояние)");
            S.huntRiskLimit = l.Slider(S.huntRiskLimit, 0.10f, 0.90f);
            S.huntMaxTargets = Slider(l, "Целей охоты за раз", S.huntMaxTargets, 1, 5);
            l.GapLine();
            l.Label("Оборона базы");
            l.CheckboxLabeled("Авто-оборона: укрытия, ловушки, стены, турели", ref S.autoDefense);
            l.CheckboxLabeled("Строить крышу над ключевыми помещениями", ref S.defenseRoofs);
            S.defenseTurretCap = Slider(l, "Лимит турелей", S.defenseTurretCap, 0, 12);
            S.defenseTrapCap = Slider(l, "Лимит ловушек", S.defenseTrapCap, 0, 20);
            l.GapLine();
            l.Label("Производство и порядок");
            l.CheckboxLabeled("Крафтить нужные вещи (оружие, одежда, лекарства, компоненты)", ref S.craftItems);
            l.CheckboxLabeled("Одна главная задача за раз (не распыляться)", ref S.focusTasks);
            l.CheckboxLabeled("Строить дом по ресурсам и задаче", ref S.autoHousing);
            l.CheckboxLabeled("Закрывать только оповещения с паузой (не меню и не рабочие окна)", ref S.autoCloseWindows);
            l.CheckboxLabeled("Убирать письма и всплывающие сообщения", ref S.autoCloseNotifications);
            l.CheckboxLabeled("Снимать крышу над панелями и грядками", ref S.autoRoofs);
            l.CheckboxLabeled("Убирать пустые и дублирующиеся зоны", ref S.autoZoneCleanup);
            l.CheckboxLabeled("Умные паттерны: пещера, двери, пожарная защита", ref S.autoPatterns);
            l.CheckboxLabeled("Умный выбор грядок: плодородие, дорога, компактность, культура", ref S.autoFields);
            l.CheckboxLabeled("Шаблоны выживания: голод, зима, холод, жара, осадки, блайт", ref S.autoSurvival);
            l.GapLine();
            l.Label(Ledger.Summary());
            l.End();
            Widgets.EndScrollView();
            base.DoSettingsWindowContents(inRect);
        }

        private static int Slider(Listing_Standard l, string label, int v, int min, int max)
        {
            l.Label(label + ": " + v);
            return Mathf.RoundToInt(l.Slider(v, min, max));
        }
    }

    /// <summary>Логи, защита от исключений, мост к основному боту (через reflection — без жёсткой зависимости).</summary>
    public static class T
    {
        private static MethodInfo botInfo;
        private static bool botInfoResolved;

        public static void Info(string m) { Info(m, "действие"); }

        public static void Info(string m, string cat)
        {
            Log.Message("[TradeEmpire] " + m);
            Mind.Think(cat, m);
            try
            {
                if (!botInfoResolved)
                {
                    botInfoResolved = true;
                    var t = FindType("RimWorldBot.BotLog");
                    if (t != null) botInfo = t.GetMethod("Info", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
                if (botInfo != null) botInfo.Invoke(null, new object[] { "[Торговля] " + m });
            }
            catch { }
        }

        public static void Warn(string m) { Log.Warning("[TradeEmpire] " + m); }

        public static void Safe(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warning("[TradeEmpire] модуль '" + name + "' упал: " + e.GetType().Name + ": " + e.Message); }
        }

        public static Type FindType(string full)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(full, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        /// <summary>Основной бот включён и не на паузе?</summary>
        public static bool BotAllows()
        {
            try
            {
                var t = FindType("RimWorldBot.BotGameComponent");
                if (t == null) return true;
                var inst = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                if (inst == null) return true;
                var paused = t.GetProperty("Paused")?.GetValue(inst, null);
                if (paused is bool && (bool)paused) return false;
                var st = t.GetField("settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inst);
                var en = st?.GetType().GetField("enabled")?.GetValue(st);
                if (en is bool && !(bool)en) return false;
            }
            catch { }
            return true;
        }

        /// <summary>Читает числовую настройку основного бота (minSteelCount и т.п.).</summary>
        public static int BotInt(string field, int fallback)
        {
            try
            {
                var t = FindType("RimWorldBot.BotGameComponent");
                var inst = t?.GetField("Instance")?.GetValue(null);
                var st = t?.GetField("settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inst);
                var v = st?.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(st);
                if (v is int) return (int)v;
                if (v is float) return Mathf.RoundToInt((float)v);
            }
            catch { }
            return fallback;
        }

        public static int Count(Map map, ThingDef def)
        {
            if (def == null) return 0;
            int n = 0;
            var list = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < list.Count; i++) if (!list[i].IsForbidden(Faction.OfPlayer)) n += list[i].stackCount;
            return n;
        }

        public static ThingDef Def(string name) { return DefDatabase<ThingDef>.GetNamedSilentFail(name); }
    }

    /// <summary>Учёт торгового оборота (сохраняется в сейве).</summary>
    public static class Ledger
    {
        public static TradeComponent C { get { return Current.Game?.GetComponent<TradeComponent>(); } }

        public static string Summary()
        {
            var c = C;
            if (c == null) return "Оборот: (игра не загружена)";
            return "Оборот: продано на " + c.totalSold + ", куплено на " + c.totalBought + ", сделок: " + c.deals
                 + ", суммарный оборот: " + (c.totalSold + c.totalBought);
        }

        /// <summary>Запоминает фактическую продажу: что колония реально умеет сбывать.</summary>
        public static void RecordSale(ThingDef def, int count)
        {
            if (def == null || count <= 0) return;
            var c = C;
            if (c != null) c.RecordSale(def, count);
            else Knowledge.ObserveSale(def.defName, count);
        }

        /// <summary>Спрос: сколько единиц этого товара уже удалось продать (с учётом прошлых игр).</summary>
        public static float Demand(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return 0f;
            float learned = Knowledge.DemandScore(defName);
            int sold = 0;
            var c = C;
            if (c != null && c.soldByDef != null && c.soldByDef.TryGetValue(defName, out sold)) sold = Mathf.Min(sold, 200);
            return Mathf.Max(learned, sold * 0.25f);
        }
    }
}
