using System.Collections.Generic;
using CodingRiver.UPilot.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CodingRiver.UPilot.Acceptance
{
    // Canonical-project-only real-library coverage. No optional dependency in the product assembly.
    public class UPilotNewtonsoftExecutionTests
    {
        private static object Evaluate(string code, bool emit, Dictionary<string, object> variables = null)
        {
            var context = new CSharpEvaluationContext(variables);
            return emit
                ? CSharpEmitBackend.Compile(CSharpEmitBackend.CacheKey(code, "statements", new[] { "System" }),
                    code, "statements")(context).Value
                : CSharpSubsetEngine.Evaluate(code, "statements", context).Value;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void JObjectHiddenParseReturnsDerivedType(bool emit)
        {
            var value = Evaluate("return Newtonsoft.Json.Linq.JObject.Parse(\"{}\");", emit);
            Assert.That(value, Is.TypeOf<JObject>());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void JObjectStringIndexerWritesRealJValue(bool emit)
        {
            var value = Evaluate("var obj = Newtonsoft.Json.Linq.JObject.Parse(\"{}\"); " +
                "obj[\"name\"] = new Newtonsoft.Json.Linq.JValue(\"updated\"); return obj;", emit);
            Assert.That(value, Is.TypeOf<JObject>());
            Assert.That(((JObject)value)["name"].Value<string>(), Is.EqualTo("updated"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void JTokenFormattingEnumSelectsRealOverload(bool emit)
        {
            var value = Evaluate("return Newtonsoft.Json.Linq.JToken.Parse(\"{\\\"n\\\":1}\")" +
                ".ToString(Newtonsoft.Json.Formatting.None);", emit);
            Assert.That(value, Is.EqualTo("{\"n\":1}"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SerializeObjectEvaluatesNestedArgumentOnce(bool emit, bool withFormatting)
        {
            var probe = new NewtonsoftSerializationArgumentProbe();
            var suffix = withFormatting ? ", Newtonsoft.Json.Formatting.None" : "";
            var result = Evaluate("return Newtonsoft.Json.JsonConvert.SerializeObject(input.NextValue()" + suffix + ");",
                emit, new Dictionary<string, object> { { "input", probe } });
            Assert.That(result, Is.EqualTo("{\"n\":1}"));
            Assert.That(probe.Calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SerializeObjectStructuredBindingPrefersNonParams(bool withFormatting)
        {
            var value = new JObject { ["n"] = 1 };
            var args = new List<ExecutionValue> { new ExecutionValue { Value = value } };
            if (withFormatting) args.Add(new ExecutionValue { Value = Formatting.None });

            var bound = MethodBinder.Bind(typeof(JsonConvert), "SerializeObject", true, args);
            Assert.That(bound.Method.DeclaringType, Is.EqualTo(typeof(JsonConvert)));
            Assert.That(bound.Parameters.Length, Is.EqualTo(withFormatting ? 2 : 1));
            Assert.That(bound.Parameters[0].ParameterType, Is.EqualTo(typeof(object)));
            if (withFormatting) Assert.That(bound.Parameters[1].ParameterType, Is.EqualTo(typeof(Formatting)));
            Assert.That(bound.Arguments[0], Is.SameAs(value));
            Assert.That(bound.Method.Invoke(null, bound.Arguments), Is.EqualTo("{\"n\":1}"));
        }
    }

    public sealed class NewtonsoftSerializationArgumentProbe
    {
        public int Calls;
        public JObject NextValue()
        {
            Calls++;
            return new JObject { ["n"] = 1 };
        }
    }
}
