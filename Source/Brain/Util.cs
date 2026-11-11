using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Мелкие математические и текстовые помощники. Без внешних зависимостей.</summary>
    public static class U
    {
        public static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
        public static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
        public static int ClampInt(int v, int a, int b) { return v < a ? a : v > b ? b : v; }
        public static float SafeDiv(float a, float b, float fallback) { return Math.Abs(b) < 0.0001f ? fallback : a / b; }

        public static float MoveTo(float current, float target, float maxDelta)
        {
            if (current < target) return Math.Min(target, current + maxDelta);
            return Math.Max(target, current - maxDelta);
        }

        public static string F(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        public static string F(float v, int digits) { return v.ToString("F" + digits, CultureInfo.InvariantCulture); }
        public static string Pct(float v) { return ((int)Math.Round(Clamp01(v) * 100f)).ToString(CultureInfo.InvariantCulture) + "%"; }
        public static string Sign(float v) { return (v >= 0f ? "+" : "−") + F(Math.Abs(v)); }

        public static float ParseFloat(string s, float fallback)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        public static float Avg(List<float> values, float fallback)
        {
            if (values == null || values.Count == 0) return fallback;
            float sum = 0f;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }

        /// <summary>Линейный тренд по последним точкам (значение в сутки).</summary>
        public static float Slope(List<float> values, float dtDays)
        {
            if (values == null || values.Count < 2 || dtDays <= 0f) return 0f;
            int n = values.Count;
            float first = values[0], last = values[n - 1];
            return (last - first) / dtDays;
        }
    }

    /// <summary>Защитный доступ к чужому API через reflection: если поле/метод исчезли в другой версии — просто false.</summary>
    public static class R
    {
        public static Type Type(string full)
        {
            try { return T.FindType(full); } catch { return null; }
        }

        public static object Get(object target, string member)
        {
            if (target == null || string.IsNullOrEmpty(member)) return null;
            try
            {
                Type t = target.GetType();
                FieldInfo f = t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(target);
                PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanRead) return p.GetValue(target, null);
            }
            catch { }
            return null;
        }

        public static bool Set(object target, string member, object value)
        {
            if (target == null || string.IsNullOrEmpty(member)) return false;
            try
            {
                Type t = target.GetType();
                FieldInfo f = t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) { f.SetValue(target, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture)); return true; }
                PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite) { p.SetValue(target, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture), null); return true; }
            }
            catch { }
            return false;
        }

        public static object Call(object target, string method, params object[] args)
        {
            if (target == null || string.IsNullOrEmpty(method)) return null;
            try
            {
                MethodInfo m = FindMethod(target.GetType(), method, args, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return m != null ? m.Invoke(target, args) : null;
            }
            catch { return null; }
        }

        public static object CallStatic(string typeFull, string method, params object[] args)
        {
            try
            {
                Type t = Type(typeFull);
                if (t == null) return null;
                MethodInfo m = FindMethod(t, method, args, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return m != null ? m.Invoke(null, args) : null;
            }
            catch { return null; }
        }

        private static MethodInfo FindMethod(Type t, string name, object[] args, BindingFlags flags)
        {
            int argc = args == null ? 0 : args.Length;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                if (m.Name != name || m.GetParameters().Length != argc) continue;
                try
                {
                    ParameterInfo[] ps = m.GetParameters();
                    bool ok = true;
                    for (int i = 0; i < argc; i++)
                    {
                        if (args[i] == null) continue;
                        if (!ps[i].ParameterType.IsInstanceOfType(args[i]) && ps[i].ParameterType != args[i].GetType()) { ok = false; break; }
                    }
                    if (ok) return m;
                }
                catch { }
            }
            return null;
        }

        public static bool IsTrue(object v) { return v is bool && (bool)v; }
    }

    /// <summary>
    /// Предохранитель модуля: после серии исключений модуль отключается на время,
    /// чтобы один баг не заливал лог и не тормозил игру.
    /// </summary>
    public class Breaker
    {
        private readonly string name;
        private int errors;
        private int disabledUntil;
        private string lastError;

        public Breaker(string name) { this.name = name; }

        public bool Allowed
        {
            get
            {
                int now = 0;
                try { if (Find.TickManager != null) now = Find.TickManager.TicksGame; } catch { }
                return now >= disabledUntil;
            }
        }

        public void Run(Action a, int cooldownTicks)
        {
            if (!Allowed) return;
            try
            {
                a();
                if (errors > 0) errors--;
            }
            catch (Exception e)
            {
                errors++;
                lastError = e.GetType().Name + ": " + e.Message;
                if (errors >= 3)
                {
                    int now = 0;
                    try { now = Find.TickManager.TicksGame; } catch { }
                    disabledUntil = now + Math.Max(600, cooldownTicks);
                    errors = 0;
                    T.Warn("Модуль «" + name + "» временно отключён после повторных ошибок (" + lastError + ").");
                    Mind.Think("сбой", "Модуль «" + name + "» отключён на время: " + lastError);
                }
                else T.Warn("Модуль «" + name + "»: " + lastError);
            }
        }
    }
}
