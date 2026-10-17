using System;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class AutomationEvidenceValuesTests
    {
        [Test] public void ProjectsOnlyOrderedNumericComponents()
        {
            Assert.That(AutomationEvidenceValues.Components(new Vector2(1, 2)), Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(AutomationEvidenceValues.Components(new Vector3(1, 2, 3)), Is.EqualTo(new[] { 1f, 2f, 3f }));
            Assert.That(AutomationEvidenceValues.Components(new Vector4(1, 2, 3, 4)), Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
            Assert.That(AutomationEvidenceValues.Components(new Quaternion(1, 2, 3, 4)), Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
            Assert.That(AutomationEvidenceValues.Components(new Color(1, 2, 3, 4)), Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
            Assert.That(AutomationEvidenceValues.Components(new Rect(1, 2, 3, 4)), Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        public void RejectsNonfiniteWithTypeAndComponent(float value)
        {
            Assert.That(Assert.Throws<ArgumentException>(() => AutomationEvidenceValues.Components(new Vector3(0, value, 0))).Message, Does.Contain("Vector3.y"));
            Assert.That(Assert.Throws<ArgumentException>(() => AutomationEvidenceValues.Components(new Color(0, 0, value, 0))).Message, Does.Contain("Color.b"));
            Assert.That(Assert.Throws<ArgumentException>(() => AutomationEvidenceValues.Components(new Rect(0, 0, 1, value))).Message, Does.Contain("Rect.height"));
        }
    }
}
