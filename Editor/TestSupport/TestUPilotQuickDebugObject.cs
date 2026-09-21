// SPDX-License-Identifier: MIT
using System;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    public sealed class TestUPilotQuickDebugObject
    {
        private readonly Task _asyncCompletion;
        internal Task PendingOperation { get; private set; } = Task.CompletedTask;
        public TestUPilotQuickDebugObject() : this(Task.CompletedTask) { }
        internal TestUPilotQuickDebugObject(Task asyncCompletion) { _asyncCompletion = asyncCompletion; }
        public int Calls { get; private set; }
        public static int Add(int first, int second, TestUPilotQuickDebugObject counter)
        {
            counter.Calls++;
            return first + second;
        }
        public int Count(int value) { Calls++; return value; }
        public string Choose(int value) { Calls++; return "int:" + value; }
        public string Choose(string value) { Calls++; return "string:" + value; }
        public T Identity<T>(T value) { Calls++; return value; }
        public void RefOut(ref int value, out string text) { Calls++; value += 2; text = value.ToString(); }
        public Task<int> TaskValue() => StartOperation(17);
        public ValueTask<int> ValueTaskValue() => new ValueTask<int>(StartOperation(19));
        public TestUPilotQuickDebugObject Self() { Calls++; return this; }
        public int Throw() { Calls++; throw new InvalidOperationException("QUICK_DEBUG_EXPECTED_FAILURE"); }

        private Task<int> StartOperation(int value)
        {
            Calls++;
            var task = CompleteOperation(value);
            PendingOperation = task;
            return task;
        }

        private async Task<int> CompleteOperation(int value)
        {
            await Task.Yield();
            await _asyncCompletion;
            return value;
        }
    }
}
