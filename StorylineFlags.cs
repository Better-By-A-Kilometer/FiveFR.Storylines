using System;
using CitizenFX.Core.Native;

namespace FiveFR.Storylines
{
    /// <summary>How Episode 1 ended. Fled is added on top of the resolution when she bolted for the hall.</summary>
    [Flags]
    public enum Ep1Outcome
    {
        None = 0,
        Warned = 1,
        Cited = 2,
        Arrested = 4,
        Fled = 8
    }

    /// <summary>Persistent storyline flags (resource KVP), so later episodes can remember earlier choices.</summary>
    public static class StorylineFlags
    {
        private const string Prefix = "fivefr_storylines:";

        public static void Set<T>(string key, T value) where T : struct, Enum =>
            API.SetResourceKvp(Prefix + key, value.ToString());

        public static int GetCount(string key) =>
            int.TryParse(API.GetResourceKvpString(Prefix + key), out var n) ? n : 0;

        public static void Increment(string key) =>
            API.SetResourceKvp(Prefix + key, (GetCount(key) + 1).ToString());

        public static T Get<T>(string key, T fallback = default) where T : struct, Enum
        {
            var raw = API.GetResourceKvpString(Prefix + key);
            return !string.IsNullOrEmpty(raw) && Enum.TryParse(raw, out T parsed) ? parsed : fallback;
        }
    }
}
