using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Старый публичный фасад памяти. Оставлен, чтобы остальные модули (кризисы, дневник)
    /// продолжали работать; настоящая память теперь в Knowledge (Brain/Learning.cs).
    /// </summary>
    public static class Memory
    {
        public class Arm { public int n; public double mean; }

        public static readonly Dictionary<string, Arm> Arms = new Dictionary<string, Arm>();

        public static string Dir { get { return Knowledge.Dir; } }
        public static string JournalPath { get { return Knowledge.JournalPath; } }

        public static void Load()
        {
            Knowledge.Load();
            Refresh();
        }

        public static void Save()
        {
            Knowledge.Save(true);
            Refresh();
        }

        public static void Update(string key, double reward)
        {
            if (string.IsNullOrEmpty(key)) return;
            string field = key;
            int arm = 0;
            int hash = key.IndexOf('#');
            if (hash > 0)
            {
                field = key.Substring(0, hash);
                arm = U.ParseInt(key.Substring(hash + 1), 0);
            }
            Knowledge.ObserveArm(field, arm, "*", reward);
            Arm a;
            if (!Arms.TryGetValue(key, out a)) { a = new Arm(); Arms[key] = a; }
            a.n++;
            a.mean += (reward - a.mean) / a.n;
        }

        public static void Journal(string text) { Knowledge.Journal(text); }

        private static void Refresh()
        {
            Arms.Clear();
            foreach (var kv in Knowledge.Arms)
            {
                if (!kv.Key.EndsWith("|*", StringComparison.Ordinal)) continue;
                string[] parts = kv.Key.Split('|');
                if (parts.Length < 3) continue;
                Arms[parts[0] + "#" + parts[1]] = new Arm { n = kv.Value.n, mean = kv.Value.Mean };
            }
        }
    }

    /// <summary>Доступ к BotSettings основного бота через reflection.</summary>
    public static class BotSettingsAccess
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static object Settings()
        {
            Type t = T.FindType("RimWorldBot.BotGameComponent");
            if (t == null) return null;
            FieldInfo instField = t.GetField("Instance", AnyStatic);
            object inst = instField != null ? instField.GetValue(null) : null;
            if (inst == null) return null;
            // поле settings приватное — без NonPublic его не достать
            FieldInfo setField = t.GetField("settings", Any);
            return setField != null ? setField.GetValue(inst) : null;
        }

        public static bool Set(string field, object value)
        {
            try
            {
                object st = Settings();
                if (st == null) return false;
                FieldInfo f = st.GetType().GetField(field, Any);
                if (f == null) return false;
                f.SetValue(st, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
                // основной бот сам приводит настройки в допустимые рамки
                try
                {
                    MethodInfo san = st.GetType().GetMethod("Sanitize", Any);
                    if (san != null) san.Invoke(st, null);
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public static object Get(string field)
        {
            try
            {
                object st = Settings();
                return st != null ? st.GetType().GetField(field, Any)?.GetValue(st) : null;
            }
            catch { return null; }
        }

        public static bool Has(string field)
        {
            try
            {
                object st = Settings();
                return st != null && st.GetType().GetField(field, Any) != null;
            }
            catch { return false; }
        }
    }
}
