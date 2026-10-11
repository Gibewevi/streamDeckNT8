using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static void Recheck(JournalSeal seal)
    {
        typeof(JournalSeal).GetField("_lastKeyLook", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(seal, DateTime.MinValue);
    }
    private static string Signature(Dictionary<string, object> record, string key, string previous)
    {
        var canonical = "exec\u001f" + record["execId"] + new string('\u001f', 14);
        var payload = Convert.ToInt64(record["seq"]).ToString(CultureInfo.InvariantCulture) + "\u001f" + previous + "\u001f" + canonical;
        var bytes = new byte[32];
        for (var i = 0; i < 32; i++) bytes[i] = Convert.ToByte(key.Substring(i * 2, 2), 16);
        using (var hmac = new HMACSHA256(bytes))
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
    }
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tradedeck-seal-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "journal.key");
        var firstKey = new string('1', 64);
        var secondKey = new string('2', 64);
        File.WriteAllText(keyPath, firstKey);
        var spool = Path.Combine(directory, "executions-fixture.ndjson");
        File.WriteAllText(spool, "unsent-fill\n");
        var seal = new JournalSeal(directory);
        var first = new Dictionary<string, object> { { "execId", "first" } };
        seal.Stamp(first);
        Check((string)first["sig"] == Signature(first, firstKey, ""), "initial key signs fills");
        File.WriteAllText(keyPath, secondKey);
        Recheck(seal);
        var second = new Dictionary<string, object> { { "execId", "second" } };
        seal.Stamp(second);
        Check((string)second["sig"] == Signature(second, secondKey, ""), "running add-on rotates key and starts new chain");
        Check(Directory.GetDirectories(Path.Combine(directory, "key-archives")).Length == 1, "previous key and execution state archived");
        Check(File.ReadAllText(spool) == "unsent-fill\n", "pending execution spool retained");
        Recheck(seal);
        var third = new Dictionary<string, object> { { "execId", "third" } };
        seal.Stamp(third);
        Check((string)third["sig"] == Signature(third, secondKey, (string)second["sig"]), "same key keeps chain continuity");
        var restarted = new JournalSeal(directory);
        var fourth = new Dictionary<string, object> { { "execId", "fourth" } };
        restarted.Stamp(fourth);
        Check((string)fourth["sig"] == Signature(fourth, secondKey, (string)third["sig"]), "restart preserves existing production chain");
        File.WriteAllText(keyPath, firstKey);
        var changedDuringStop = new JournalSeal(directory);
        var afterStop = new Dictionary<string, object> { { "execId", "after-stop" } };
        changedDuringStop.Stamp(afterStop);
        Check((string)afterStop["sig"] == Signature(afterStop, firstKey, ""), "key rotated while stopped starts correct new chain");
        File.Delete(keyPath);
        Recheck(restarted);
        var unsigned = new Dictionary<string, object> { { "execId", "unsigned" } };
        restarted.Stamp(unsigned);
        Check(!unsigned.ContainsKey("sig"), "missing key never reuses old credentials");
        File.WriteAllText(keyPath, firstKey);
        Recheck(restarted);
        var recovered = new Dictionary<string, object> { { "execId", "recovered" } };
        restarted.Stamp(recovered);
        Check((string)recovered["sig"] == Signature(recovered, firstKey, ""), "pairing after missing key recovers");
    }
}

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities
{
    public static class SdLogger
    {
        public static void Info(string message, params object[] args) { }
        public static void Warn(string message, params object[] args) { }
        public static void Fail(string category, Exception error, string message) { throw error; }
    }
}
