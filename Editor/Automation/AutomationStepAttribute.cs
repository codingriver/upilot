using System;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Registers a concrete step. Constructors and Validate must have no side effects.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class AutomationStepAttribute : Attribute
    {
        public string Id { get; }
        public string Description { get; set; } = "";
        public string ArgumentsExample { get; set; } = "";
        public double TimeoutSeconds { get; set; } = 30;
        public double PollIntervalSeconds { get; set; } = 0.1;
        public AutomationStepAttribute(string id) { Id = id; }
    }
}
