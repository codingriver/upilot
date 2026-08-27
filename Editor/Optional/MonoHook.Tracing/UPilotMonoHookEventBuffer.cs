// -----------------------------------------------------------------------
// UPilot Editor - bounded MonoHook event buffer.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace CodingRiver.UPilot
{
    public sealed class UPilotMonoHookEventBuffer
    {
        private readonly Queue<UPilotMonoHookEvent> _events = new Queue<UPilotMonoHookEvent>();
        private readonly int _capacity;
        private long _nextSequence;
        private int _droppedCount;

        public UPilotMonoHookEventBuffer(int capacity = 2048)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Capacity => _capacity;
        public int Count => _events.Count;
        public int DroppedCount => _droppedCount;

        public long Add(UPilotMonoHookEvent value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            value.sequence = ++_nextSequence;
            if (_events.Count >= _capacity)
            {
                _events.Dequeue();
                _droppedCount++;
            }
            _events.Enqueue(value);
            return value.sequence;
        }

        public List<UPilotMonoHookEvent> Read(int maxCount = 256)
        {
            if (maxCount <= 0) return new List<UPilotMonoHookEvent>();

            var result = new List<UPilotMonoHookEvent>(Math.Min(maxCount, _events.Count));
            while (result.Count < maxCount && _events.Count > 0)
                result.Add(_events.Dequeue());
            return result;
        }

        public List<UPilotMonoHookEvent> Snapshot(int maxCount = 256)
        {
            if (maxCount <= 0 || _events.Count == 0)
                return new List<UPilotMonoHookEvent>();

            var values = _events.ToArray();
            int start = Math.Max(0, values.Length - maxCount);
            var result = new List<UPilotMonoHookEvent>(values.Length - start);
            for (int i = start; i < values.Length; i++)
                result.Add(values[i]);
            return result;
        }

        public void MarkDropped()
        {
            _droppedCount++;
        }

        public void Clear()
        {
            _events.Clear();
            _droppedCount = 0;
        }
    }
}
