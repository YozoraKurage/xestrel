using System;
using System.Collections.Generic;
using UnityEngine;

namespace Xestrel.Core
{
    internal enum XestrelLogCategory
    {
        Isolate,
        UI
    }

    internal enum XestrelLogLevel { Info, Warn, Error }

    internal readonly struct XestrelLogEntry
    {
        public readonly DateTime TimestampUtc;
        public readonly XestrelLogCategory Category;
        public readonly XestrelLogLevel Level;
        public readonly string Message;

        public XestrelLogEntry(DateTime ts, XestrelLogCategory c, XestrelLogLevel l, string m)
        {
            TimestampUtc = ts;
            Category = c;
            Level = l;
            Message = m;
        }

        public override string ToString() =>
            $"[xestrel/{Category}] {Message}";
    }

    internal static class XestrelLog
    {
        private const int RingCapacity = 256;
        private static readonly Queue<XestrelLogEntry> _ring = new Queue<XestrelLogEntry>(RingCapacity);
        private static readonly object _gate = new object();

        public static event Action<XestrelLogEntry> Entry;

        public static IReadOnlyCollection<XestrelLogEntry> Snapshot()
        {
            lock (_gate) return new List<XestrelLogEntry>(_ring);
        }

        public static void Info(XestrelLogCategory c, string msg) => Emit(c, XestrelLogLevel.Info, msg);
        public static void Warn(XestrelLogCategory c, string msg) => Emit(c, XestrelLogLevel.Warn, msg);
        public static void Error(XestrelLogCategory c, string msg) => Emit(c, XestrelLogLevel.Error, msg);

        private static void Emit(XestrelLogCategory c, XestrelLogLevel l, string msg)
        {
            var entry = new XestrelLogEntry(DateTime.UtcNow, c, l, msg);
            lock (_gate)
            {
                if (_ring.Count >= RingCapacity) _ring.Dequeue();
                _ring.Enqueue(entry);
            }
            switch (l)
            {
                case XestrelLogLevel.Info: Debug.Log(entry.ToString()); break;
                case XestrelLogLevel.Warn: Debug.LogWarning(entry.ToString()); break;
                case XestrelLogLevel.Error: Debug.LogError(entry.ToString()); break;
            }
            Entry?.Invoke(entry);
        }
    }
}
