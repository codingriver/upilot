using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowParsingAndPlanningTests
    {
        [Test]
        public void Parser_BuildsExecutionPlan_FromCsvCase()
        {
            string yamlPath = Path.GetFullPath("Assets/Examples/Yaml/02-data-driven-csv.yaml");
            var parser = new YamlTestCaseParser();
            TestCaseDefinition definition = parser.ParseFile(yamlPath);
            var builder = new ExecutionPlanBuilder(new SelectorCompiler(), new ActionRegistry());
            ExecutionPlan plan = builder.Build(definition, new TestOptions());

            Assert.That(plan.CaseName, Is.EqualTo("CSV Driven Login"));
            Assert.That(plan.Steps.Count, Is.EqualTo(8));
            Assert.That(plan.Steps[0].Parameters["value"], Is.EqualTo("alice"));
            Assert.That(plan.Steps[4].Parameters["value"], Is.EqualTo("bob"));
        }

        [Test]
        public void Parser_CapturesAdditionalActionParameters()
        {
            const string yaml = @"
name: Custom Parameter Case
steps:
  - name: Login
    action: custom_login
    username_selector: '#username-input'
    password_selector: '#password-input'
    button_selector: '#login-button'
    username: 'alice'
    password: 'secret'
";

            TestCaseDefinition definition = new YamlTestCaseParser().Parse(yaml, "inline.yaml");

            Assert.That(definition.Steps, Has.Count.EqualTo(1));
            Assert.That(definition.Steps[0].Parameters["username_selector"], Is.EqualTo("#username-input"));
            Assert.That(definition.Steps[0].Parameters["button_selector"], Is.EqualTo("#login-button"));
        }

        [Test]
        public void Parser_RejectsStepWithActionAndLoop()
        {
            const string yaml = @"
name: Invalid Case
steps:
  - name: Invalid step
    action: click
    repeat_while:
      condition:
        exists: '#target'
      steps:
        - action: wait
          duration: '50ms'
";

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new YamlTestCaseParser().Parse(yaml, "invalid.yaml"));

            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
            Assert.That(ex.Message, Does.Contain("repeat_while"));
        }

        [Test]
        public void TemplateRenderer_ThrowsOnMissingVariable()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() =>
                TemplateRenderer.Render("Hello {{ username }}", new Dictionary<string, string>(), "Missing variable step"));

            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestDataVariableMissing));
        }

        [Test]
        public void DurationParser_ParsesMillisecondsAndSeconds()
        {
            Assert.That(DurationParser.ParseToMilliseconds("50ms", "step"), Is.EqualTo(50));
            Assert.That(DurationParser.ParseToMilliseconds("1.5s", "step"), Is.EqualTo(1500));
        }

        [Test]
        public void TestDataResolver_LoadsJsonRows()
        {
            TestCaseDefinition definition = new YamlTestCaseParser().Parse(@"
name: Json Data
data:
  from_json: users.json
steps:
  - action: wait
    duration: '10ms'
", Path.GetFullPath("Assets/Examples/Yaml/05-custom-action-and-json.yaml"));

            List<Dictionary<string, string>> rows = TestDataResolver.ResolveRows(definition);

            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0]["username"], Is.EqualTo("alice"));
            Assert.That(rows[1]["expected"], Does.Contain("bob"));
        }

        [Test]
        public void TestDataResolver_LoadsUtf8BomCsvRows()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                string csvPath = Path.Combine(tempDirectory, "users.csv");
                File.WriteAllText(csvPath, "username,expected\r\nalice,ok\r\n", new UTF8Encoding(true));

                var definition = new TestCaseDefinition
                {
                    Name = "CSV BOM",
                    SourceFile = Path.Combine(tempDirectory, "case.yaml"),
                    Data = new DataSourceDefinition
                    {
                        FromCsv = "users.csv",
                    },
                    Steps = new List<StepDefinition>
                    {
                        new StepDefinition
                        {
                            Action = "wait",
                            Duration = "10ms",
                        },
                    },
                };

                List<Dictionary<string, string>> rows = TestDataResolver.ResolveRows(definition);

                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].ContainsKey("username"), Is.True);
                Assert.That(rows[0].ContainsKey("\ufeffusername"), Is.False);
                Assert.That(rows[0]["username"], Is.EqualTo("alice"));
                Assert.That(rows[0]["expected"], Is.EqualTo("ok"));
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [Test]
        public void SelectorCompiler_ParsesPseudoAndChildSelectors()
        {
            SelectorExpression expression = new SelectorCompiler().Compile("#panel > .item:first-child");

            Assert.That(expression.Segments.Count, Is.EqualTo(3));
            Assert.That(expression.Segments[0].TokenType, Is.EqualTo(SelectorTokenType.Id));
            Assert.That(expression.Segments[1].Combinator, Is.EqualTo(SelectorCombinator.Child));
            Assert.That(expression.Segments[2].TokenType, Is.EqualTo(SelectorTokenType.Pseudo));
        }

        [Test]
        public void ExecutionPlanBuilder_ExpandsSetupMainTeardownForEveryRow()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Plan Expansion",
                SourceFile = "inline.yaml",
                Data = new DataSourceDefinition
                {
                    Rows = new List<Dictionary<string, string>>
                    {
                        new Dictionary<string, string> { ["username"] = "alice" },
                        new Dictionary<string, string> { ["username"] = "bob" },
                    },
                },
                Fixture = new FixtureDefinition
                {
                    Setup = new List<StepDefinition>
                    {
                        new StepDefinition { Action = "wait", Duration = "10ms" },
                    },
                    Teardown = new List<StepDefinition>
                    {
                        new StepDefinition { Action = "wait", Duration = "10ms" },
                    },
                },
                Steps = new List<StepDefinition>
                {
                    new StepDefinition
                    {
                        Name = "Type {{ username }}",
                        Action = "type_text_fast",
                        Selector = "#username-input",
                        Value = "{{ username }}",
                    },
                },
            };

            ExecutionPlan plan = new ExecutionPlanBuilder(new SelectorCompiler(), new ActionRegistry()).Build(definition, new TestOptions());

            Assert.That(plan.Steps, Has.Count.EqualTo(6));
            Assert.That(plan.Steps[0].Phase, Is.EqualTo(StepPhase.Setup));
            Assert.That(plan.Steps[1].Phase, Is.EqualTo(StepPhase.Main));
            Assert.That(plan.Steps[1].Parameters["value"], Is.EqualTo("alice"));
            Assert.That(plan.Steps[4].Parameters["value"], Is.EqualTo("bob"));
            Assert.That(plan.Steps[5].Phase, Is.EqualTo(StepPhase.Teardown));
        }

        [Test]
        public void ActionRegistry_ResolvesBuiltInAndCustomActions()
        {
            var registry = new ActionRegistry();

            Assert.That(registry.HasAction("click"), Is.True);
            Assert.That(registry.HasAction("execute_command"), Is.True);
            Assert.That(registry.HasAction("validate_command"), Is.True);
            Assert.That(registry.Resolve("click"), Is.Not.Null);
            Assert.That(registry.Resolve("execute_command"), Is.Not.Null);
            Assert.That(registry.Resolve("validate_command"), Is.Not.Null);
            Assert.That(registry.HasAction("custom_login"), Is.True);
            Assert.That(registry.Resolve("custom_login"), Is.TypeOf<CustomLoginAction>());
        }

        [Test]
        public void TestAssets_CanBeLoadedFromProject()
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SampleLoginWindow.UxmlPath), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<StyleSheet>(SampleLoginWindow.UssPath), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SampleInteractionWindow.UxmlPath), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<StyleSheet>(SampleInteractionWindow.UssPath), Is.Not.Null);
        }

        [Test]
        public void SchemaValidator_RejectsEmptyName()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(new TestCaseDefinition { Name = "", Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } } }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsMissingSteps()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(new TestCaseDefinition { Name = "No Steps" }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsNegativeTimeout()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(new TestCaseDefinition { Name = "Bad Timeout", Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } }, TimeoutMs = -1 }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsEmptyHostWindowType()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Empty Host",
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
                Fixture = new FixtureDefinition { HostWindow = new HostWindowDefinition { Type = "  " } },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsDuplicateDataSources()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Duplicate Data",
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
                Data = new DataSourceDefinition { FromCsv = "a.csv", FromJson = "a.json" },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsStepWithoutActionAndLoop()
        {
            var definition = new TestCaseDefinition
            {
                Name = "No Action",
                Steps = new List<StepDefinition> { new StepDefinition() },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsInvalidTimeoutLiteral()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Invalid Timeout",
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Timeout = "abc" } },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsEmptyIfExists()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Empty If",
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms", If = new ConditionDefinition() } },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsEmptyLoopCondition()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Empty Loop",
                Steps = new List<StepDefinition>
                {
                    new StepDefinition
                    {
                        RepeatWhile = new LoopDefinition
                        {
                            Condition = new ConditionDefinition(),
                            Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
                        },
                    },
                },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsLoopWithoutSteps()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Loop No Steps",
                Steps = new List<StepDefinition>
                {
                    new StepDefinition
                    {
                        RepeatWhile = new LoopDefinition
                        {
                            Condition = new ConditionDefinition { Exists = "#foo" },
                            Steps = new List<StepDefinition>(),
                        },
                    },
                },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void SchemaValidator_RejectsLoopMaxIterationsOutOfRange()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Loop MaxIterations",
                Steps = new List<StepDefinition>
                {
                    new StepDefinition
                    {
                        RepeatWhile = new LoopDefinition
                        {
                            Condition = new ConditionDefinition { Exists = "#foo" },
                            Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
                            MaxIterations = 1001,
                        },
                    },
                },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void DurationParser_AcceptsZeroAndUpperBoundary()
        {
            Assert.That(DurationParser.ParseToMilliseconds("0ms", "step"), Is.EqualTo(0));
            Assert.That(DurationParser.ParseToMilliseconds("600s", "step"), Is.EqualTo(600000));
        }

        [Test]
        public void DurationParser_RejectsNegativeAndOverBoundary()
        {
            Assert.Throws<UnityUIFlowException>(() => DurationParser.ParseToMilliseconds("-1ms", "step"));
            Assert.Throws<UnityUIFlowException>(() => DurationParser.ParseToMilliseconds("600001ms", "step"));
            Assert.Throws<UnityUIFlowException>(() => DurationParser.ParseToMilliseconds("1.5x", "step"));
            Assert.Throws<UnityUIFlowException>(() => DurationParser.ParseToMilliseconds(string.Empty, "step"));
        }

        [Test]
        public void ExecutionPlanBuilder_RejectsOver5000Steps()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Huge Case",
                SourceFile = "huge.yaml",
                Steps = new List<StepDefinition>(),
            };
            for (int i = 0; i < 5001; i++)
            {
                definition.Steps.Add(new StepDefinition { Action = "wait", Duration = "1ms" });
            }

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new ExecutionPlanBuilder(new SelectorCompiler(), new ActionRegistry()).Build(definition, new TestOptions()));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void ExecutionPlanBuilder_RejectsUnregisteredAction()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Unregistered",
                SourceFile = "bad.yaml",
                Steps = new List<StepDefinition> { new StepDefinition { Action = "not_a_real_action" } },
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new ExecutionPlanBuilder(new SelectorCompiler(), new ActionRegistry()).Build(definition, new TestOptions()));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.ActionNotRegistered));
        }

        [Test]
        public void TemplateRenderer_PreservesSpecialCharacters()
        {
            string result = TemplateRenderer.Render("Hello @#$%", new Dictionary<string, string>(), "step");
            Assert.That(result, Is.EqualTo("Hello @#$%"));
        }

        [Test]
        public void YamlObjectReader_AsMap_ReturnsDictionary()
        {
            var dict = new Dictionary<string, object> { ["key"] = "value" };
            Dictionary<string, object> result = YamlObjectReader.AsMap(dict, "test");
            Assert.That(result["key"], Is.EqualTo("value"));
        }

        [Test]
        public void YamlObjectReader_AsSequence_ReturnsList()
        {
            var list = new List<object> { "a", "b" };
            List<object> result = YamlObjectReader.AsSequence(list, "test");
            Assert.That(result, Has.Count.EqualTo(2));
        }

        [Test]
        public void YamlObjectReader_GetInt_ReturnsExpectedValue()
        {
            var map = new Dictionary<string, object> { ["num"] = 42 };
            Assert.That(YamlObjectReader.GetInt(map, "num", false, 0), Is.EqualTo(42));
            Assert.That(YamlObjectReader.GetInt(map, "missing", false, 7), Is.EqualTo(7));
        }

        [Test]
        public void YamlObjectReader_GetNullableInt_ReturnsNullWhenMissing()
        {
            var map = new Dictionary<string, object>();
            Assert.That(YamlObjectReader.GetNullableInt(map, "missing", false), Is.Null);
        }

        [Test]
        public void YamlTestCaseParser_ParseFile_RejectsNonYamlExtension()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "case.yml");
            File.WriteAllText(path, "name: Test\n");

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new YamlTestCaseParser().ParseFile(path));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCasePathInvalid));
        }

        [Test]
        public void YamlTestCaseParser_ParseFile_ThrowsWhenFileMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"), "missing.yaml");
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new YamlTestCaseParser().ParseFile(path));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseFileNotFound));
        }

        [Test]
        public void YamlTestCaseParser_ParseFile_ThrowsWhenEmpty()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "empty.yaml");
            File.WriteAllText(path, "   \n");

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new YamlTestCaseParser().ParseFile(path));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.YamlParseError));
        }

        [Test]
        public void YamlTestCaseParser_Parse_ThrowsOnBrokenYamlSyntax()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new YamlTestCaseParser().Parse("name: [ broken yaml", "inline.yaml"));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.YamlParseError));
        }

        [Test]
        public void TestDataResolver_ResolvesInlineRows()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Inline",
                SourceFile = "inline.yaml",
                Data = new DataSourceDefinition
                {
                    Rows = new List<Dictionary<string, string>>
                    {
                        new Dictionary<string, string> { ["role"] = "admin" },
                        new Dictionary<string, string> { ["role"] = "guest" },
                    },
                },
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
            };

            List<Dictionary<string, string>> rows = TestDataResolver.ResolveRows(definition);
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0]["role"], Is.EqualTo("admin"));
            Assert.That(rows[1]["role"], Is.EqualTo("guest"));
        }

        [Test]
        public void TestDataResolver_LoadCsv_ColumnMismatchThrows()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string csvPath = Path.Combine(tempDir, "bad.csv");
            File.WriteAllText(csvPath, "a,b\nc\n");

            var definition = new TestCaseDefinition
            {
                Name = "BadCsv",
                SourceFile = Path.Combine(tempDir, "case.yaml"),
                Data = new DataSourceDefinition { FromCsv = "bad.csv" },
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestDataResolver.ResolveRows(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void TestDataResolver_LoadCsv_EmptyFileReturnsEmpty()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string csvPath = Path.Combine(tempDir, "empty.csv");
            File.WriteAllText(csvPath, "");

            var definition = new TestCaseDefinition
            {
                Name = "EmptyCsv",
                SourceFile = Path.Combine(tempDir, "case.yaml"),
                Data = new DataSourceDefinition { FromCsv = "empty.csv" },
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
            };

            List<Dictionary<string, string>> rows = TestDataResolver.ResolveRows(definition);
            Assert.That(rows, Is.Empty);
        }

        [Test]
        public void SelectorCompiler_RejectsVariousSyntaxErrors()
        {
            var compiler = new SelectorCompiler();
            Assert.Throws<UnityUIFlowException>(() => compiler.Compile(""), "Empty");
            Assert.Throws<UnityUIFlowException>(() => compiler.Compile("#panel > "), "Trailing child combinator");
            Assert.Throws<UnityUIFlowException>(() => compiler.Compile("[attr"), "Unclosed bracket");
            Assert.Throws<UnityUIFlowException>(() => compiler.Compile("[attr]"), "Missing value in attribute");
            Assert.Throws<UnityUIFlowException>(() => compiler.Compile("# "), "Missing identifier after #");
        }

        [Test]
        public void ExecutionPlanBuilder_CompilesLoopAndConditionDetails()
        {
            var definition = new TestCaseDefinition
            {
                Name = "LoopCompile",
                SourceFile = "loop.yaml",
                Steps = new List<StepDefinition>
                {
                    new StepDefinition
                    {
                        Name = "Loop Step",
                        RepeatWhile = new LoopDefinition
                        {
                            Condition = new ConditionDefinition { Exists = "#target-{{ idx }}" },
                            MaxIterations = 5,
                            Steps = new List<StepDefinition>
                            {
                                new StepDefinition { Action = "wait", Duration = "10ms" },
                            },
                        },
                        If = new ConditionDefinition { Exists = "#foo" },
                    },
                },
                Data = new DataSourceDefinition
                {
                    Rows = new List<Dictionary<string, string>>
                    {
                        new Dictionary<string, string> { ["idx"] = "1" },
                    },
                },
            };

            ExecutionPlan plan = new ExecutionPlanBuilder(new SelectorCompiler(), new ActionRegistry()).Build(definition, new TestOptions());
            Assert.That(plan.Steps[0].Kind, Is.EqualTo(ExecutableStepKind.Loop));
            Assert.That(plan.Steps[0].Loop.MaxIterations, Is.EqualTo(5));
            Assert.That(plan.Steps[0].Loop.Condition.SelectorExpression.Raw, Is.EqualTo("#target-1"));
            Assert.That(plan.Steps[0].Loop.Steps, Is.Not.Empty);
            Assert.That(plan.Steps[0].Condition.SelectorExpression.Raw, Is.EqualTo("#foo"));
        }

        [Test]
        public void YamlObjectReader_AsMap_ThrowsOnNonDictionary()
        {
            Assert.Throws<UnityUIFlowException>(() => YamlObjectReader.AsMap("string", "test"));
            Assert.Throws<UnityUIFlowException>(() => YamlObjectReader.AsMap(42, "test"));
        }

        [Test]
        public void YamlObjectReader_GetBool_ThrowsOnInvalidString()
        {
            var map = new Dictionary<string, object> { ["flag"] = "yes" };
            Assert.Throws<UnityUIFlowException>(() => YamlObjectReader.GetBool(map, "flag", true, false));
        }

        [Test]
        public void SchemaValidator_RejectsInvalidDurationLiteral()
        {
            var definition = new TestCaseDefinition
            {
                Name = "Invalid Duration",
                Steps = new List<StepDefinition>
                {
                    new StepDefinition { Action = "wait", Duration = "abc" },
                },
            };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestCaseSchemaValidator.Validate(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestCaseSchemaInvalid));
        }

        [Test]
        public void TestDataResolver_LoadJson_ThrowsOnMissingFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var definition = new TestCaseDefinition
            {
                Name = "MissingJson",
                SourceFile = Path.Combine(tempDir, "case.yaml"),
                Data = new DataSourceDefinition { FromJson = "missing.json" },
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestDataResolver.ResolveRows(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestDataFileNotFound));
        }

        [Test]
        public void TestDataResolver_LoadJson_ThrowsOnMalformedJson()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "bad.json"), "{ not json");

            var definition = new TestCaseDefinition
            {
                Name = "BadJson",
                SourceFile = Path.Combine(tempDir, "case.yaml"),
                Data = new DataSourceDefinition { FromJson = "bad.json" },
                Steps = new List<StepDefinition> { new StepDefinition { Action = "wait", Duration = "10ms" } },
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => TestDataResolver.ResolveRows(definition));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.TestDataFileNotFound));
        }
    }
}
