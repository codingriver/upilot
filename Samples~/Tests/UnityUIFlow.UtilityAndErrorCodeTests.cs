using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowUtilityAndErrorCodeTests
    {
        [Test]
        public void Utility_SanitizeFileName_ReplacesInvalidCharacters()
        {
            Assert.That(UnityUIFlowUtility.SanitizeFileName("a/b|c"), Is.EqualTo("a_b_c"));
            Assert.That(UnityUIFlowUtility.SanitizeFileName(string.Empty), Is.EqualTo("unnamed"));
            Assert.That(UnityUIFlowUtility.SanitizeFileName(null), Is.EqualTo("unnamed"));
        }

        [Test]
        public void Utility_DurationMs_ComputesCorrectly()
        {
            DateTimeOffset start = DateTimeOffset.UtcNow;
            Assert.That(UnityUIFlowUtility.DurationMs(start, start.AddMilliseconds(150)), Is.EqualTo(150));
            Assert.That(UnityUIFlowUtility.DurationMs(start, start.AddMilliseconds(-10)), Is.EqualTo(0));
        }

        [Test]
        public void Utility_NullIfWhiteSpace_ReturnsNullForEmptyOrWhitespace()
        {
            Assert.That(UnityUIFlowUtility.NullIfWhiteSpace("  "), Is.Null);
            Assert.That(UnityUIFlowUtility.NullIfWhiteSpace("hello"), Is.EqualTo("hello"));
        }

        [Test]
        public void Utility_EnsureRelativeTo_BuildsRelativePath()
        {
            string root = Path.GetFullPath("Assets");
            string file = Path.GetFullPath("Assets/Examples/Tests/foo.cs");
            string relative = UnityUIFlowUtility.EnsureRelativeTo(root, file);
            Assert.That(relative, Does.Contain("foo.cs"));
            Assert.That(Path.IsPathRooted(relative), Is.False);
        }

        [Test]
        public void Utility_AppendDirectorySeparator_DoesNotDoubleAppend()
        {
            string path = "Assets" + Path.DirectorySeparatorChar;
            Assert.That(UnityUIFlowUtility.AppendDirectorySeparator(path), Is.EqualTo(path));
            Assert.That(UnityUIFlowUtility.AppendDirectorySeparator("Assets"), Is.EqualTo("Assets" + Path.DirectorySeparatorChar));
        }

        [Test]
        public void ErrorCodes_AllPublicConstantsAreNonEmpty()
        {
            Type type = typeof(ErrorCodes);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            Assert.That(fields.Length, Is.GreaterThan(10));

            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    string value = (string)field.GetValue(null);
                    Assert.That(string.IsNullOrWhiteSpace(value), Is.False, $"ErrorCodes.{field.Name} should not be empty");
                }
            }
        }

        [Test]
        public void ErrorCodes_MapsExpectedValues()
        {
            Assert.That(ErrorCodes.StepTimeout, Is.EqualTo("STEP_TIMEOUT"));
            Assert.That(ErrorCodes.TestCaseSchemaInvalid, Is.EqualTo("TEST_CASE_SCHEMA_INVALID"));
            Assert.That(ErrorCodes.CliArgumentInvalid, Is.EqualTo("CLI_ARGUMENT_INVALID"));
            Assert.That(ErrorCodes.ElementWaitTimeout, Is.EqualTo("ELEMENT_WAIT_TIMEOUT"));
            Assert.That(ErrorCodes.ActionNotRegistered, Is.EqualTo("ACTION_NOT_REGISTERED"));
        }
    }
}
