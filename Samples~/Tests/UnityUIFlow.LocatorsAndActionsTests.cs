using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using UiMouseButton = UnityEngine.UIElements.MouseButton;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowLocatorFixtureTests : UnityUIFlowFixture<SampleLoginWindow>
    {
        [UnityTest]
        public IEnumerator Finder_FindsElementsByMultipleSelectorTypes()
        {
            yield return null;

            var compiler = new SelectorCompiler();

            Assert.That(Finder.Find(compiler.Compile("#login-button"), Root).Element, Is.Not.Null);
            Assert.That(Finder.Find(compiler.Compile(".item:first-child"), Root).Element.name, Is.EqualTo("menu-item-1"));
            Assert.That(Finder.Find(compiler.Compile("TextField"), Root).Element, Is.TypeOf<TextField>());
            Assert.That(Finder.Find(compiler.Compile("[tooltip=Save]"), Root).Element.name, Is.EqualTo("save-button"));
            Assert.That(Finder.Find(compiler.Compile("[data-role=primary]"), Root, false).Element.name, Is.EqualTo("login-button"));
        }

        [UnityTest]
        public IEnumerator Finder_DistinguishesExistenceFromVisibility()
        {
            yield return null;

            SelectorExpression toastSelector = new SelectorCompiler().Compile("#toast-message");

            Assert.That(Finder.Exists(toastSelector, Root, false), Is.True);
            Assert.That(Finder.Exists(toastSelector, Root, true), Is.False);

            Window.ShowToastForFrames(2);
            yield return null;

            Assert.That(Finder.Exists(toastSelector, Root, true), Is.True);
        }

        [UnityTest]
        public IEnumerator WaitForElementAsync_ReturnsWhenToastBecomesVisible()
        {
            Window.ShowToastForFrames(3);

            Task<FindResult> task = Finder.WaitForElementAsync(
                new SelectorCompiler().Compile("#toast-message"),
                Root,
                new WaitOptions
                {
                    TimeoutMs = 1000,
                    PollIntervalMs = 16,
                    RequireVisible = true,
                },
                System.Threading.CancellationToken.None);

            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Element, Is.Not.Null);
                Assert.That(result.Element.name, Is.EqualTo("toast-message"));
            });
        }

        [UnityTest]
        public IEnumerator BasicLoginYaml_CompletesSuccessfullyAndCreatesAttachment()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(
                File.ReadAllText(Path.GetFullPath("Assets/Examples/Yaml/sample-01-basic-login.yaml")),
                "01-basic-login.yaml"),
                result =>
                {
                    Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                    Assert.That(result.StepResults[result.StepResults.Count - 1].Attachments, Is.Not.Empty);
                });
        }

        [UnityTest]
        public IEnumerator ClickAction_OnLoginButton_UpdatesStatusLabel()
        {
            Root.Q<TextField>("username-input").value = "alice";
            Root.Q<TextField>("password-input").value = "secret";

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#login-button",
            }));

            Assert.That(Root.Q<Label>("status-label").text, Is.EqualTo("Welcome alice"));
        }

        [UnityTest]
        public IEnumerator FocusSetValueAndAssertActions_WorkOnLoginWindow()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new FocusAction(), new Dictionary<string, string>
            {
                ["selector"] = "#username-input",
            }));

            Assert.That(Root.focusController.focusedElement, Is.SameAs(Root.Q<TextField>("username-input")));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#username-input",
                ["value"] = "bob",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#username-input",
                ["expected"] = "bob",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertEnabledAction(), new Dictionary<string, string>
            {
                ["selector"] = "#login-button",
            }));

            Root.Q<Button>("login-button").SetEnabled(false);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertDisabledAction(), new Dictionary<string, string>
            {
                ["selector"] = "#login-button",
            }));
        }

        [UnityTest]
        public IEnumerator AssertVisibleTextAndWaitActions_WorkOnLoginWindow()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertVisibleAction(), new Dictionary<string, string>
            {
                ["selector"] = "#login-button",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertTextAction(), new Dictionary<string, string>
            {
                ["selector"] = "#status-label",
                ["expected"] = "Idle",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertTextContainsAction(), new Dictionary<string, string>
            {
                ["selector"] = "#status-label",
                ["expected"] = "Id",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertNotVisibleAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toast-message",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new WaitAction(), new Dictionary<string, string>
            {
                ["duration"] = "50ms",
            }));

            Window.ShowToastForFrames(5);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new WaitForElementAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toast-message",
                ["timeout"] = "1000ms",
            }));
        }

        [UnityTest]
        public IEnumerator AssertionsAndSelectorsYaml_CompletesSuccessfully()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(
                File.ReadAllText(Path.GetFullPath("Assets/Examples/Yaml/03-assertions-and-selectors.yaml")),
                "03-assertions-and-selectors.yaml"),
                result =>
                {
                    Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                });
        }

        [UnityTest]
        public IEnumerator LoopYaml_WaitsForToastToDisappear()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(
                File.ReadAllText(Path.GetFullPath("Assets/Examples/Yaml/sample-04-conditional-and-loop.yaml")),
                "04-conditional-and-loop.yaml"),
                result =>
                {
                    Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                    Assert.That(Root.Q<Label>("toast-message").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                });
        }

        [UnityTest]
        public IEnumerator CustomActionYaml_CompletesSuccessfully()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(
                File.ReadAllText(Path.GetFullPath("Assets/Examples/Yaml/05-custom-action-and-json.yaml")),
                "05-custom-action-and-json.yaml"),
                result =>
                {
                    Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                    Assert.That(result.StepResults[0].Status, Is.EqualTo(TestStatus.Passed));
                });
        }
    }

    public sealed class UnityUIFlowActionFixtureTests : UnityUIFlowFixture<SampleInteractionWindow>
    {
        [UnityTest]
        public IEnumerator ClickAction_UpdatesButtonStatus()
        {
            Task task = ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#click-button",
            });

            yield return UnityUIFlowTestTaskUtility.Await(task);

            Assert.That(Root.Q<Label>("click-status").text, Is.EqualTo("Clicks: 1"));
            Assert.That(LastActionContext.SharedBag["inputDriver.host"].ToString(), Is.EqualTo("OfficialEditorWindowPanelSimulator"));
            Assert.That(LastActionContext.SharedBag["inputDriver.pointer"].ToString(), Is.EqualTo("PanelSimulator"));
        }

        [UnityTest]
        public IEnumerator ClickAction_SupportsModifiersAndAlternateButtons()
        {
            Task task = ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#click-button",
                ["button"] = "right",
                ["modifiers"] = "shift,ctrl",
            });

            yield return UnityUIFlowTestTaskUtility.Await(task);

            Assert.That(Root.Q<Label>("pointer-status").text, Does.Contain("button=1"));
            Assert.That(Root.Q<Label>("pointer-status").text, Does.Contain("Shift"));
            Assert.That(Root.Q<Label>("pointer-status").text, Does.Contain("Control"));
        }

        [UnityTest]
        public IEnumerator DoubleClickAction_UpdatesButtonStatusTwice()
        {
            Task task = ExecuteActionAsync(new DoubleClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#double-click-button",
            });

            yield return UnityUIFlowTestTaskUtility.Await(task);

            Assert.That(Root.Q<Label>("double-click-status").text, Is.EqualTo("Double Clicks: 2"));
        }

        [UnityTest]
        public IEnumerator TypeTextAndPressKeyActions_UpdateWindowState()
        {
            Window.FocusInput();
            yield return null;

            Task typeTask = ExecuteActionAsync(new TypeTextAction(), new Dictionary<string, string>
            {
                ["selector"] = "#interaction-input",
                ["value"] = "hello",
            });

            yield return UnityUIFlowTestTaskUtility.Await(typeTask);

            Task pressKeyTask = ExecuteActionAsync(new PressKeyAction(), new Dictionary<string, string>
            {
                ["key"] = "A",
            });

            yield return UnityUIFlowTestTaskUtility.Await(pressKeyTask);

            Assert.That(Root.Q<TextField>("interaction-input").value, Is.EqualTo("hello"));
            Assert.That(Root.Q<Label>("key-status").text, Does.Contain("A"));
            Assert.That(LastActionContext.SharedBag["inputDriver.keyboard"].ToString(), Is.EqualTo("PanelSimulator"));
        }

        [UnityTest]
        public IEnumerator PressKeyAction_WithoutFocusedElement_DispatchesToRoot()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new PressKeyAction(), new Dictionary<string, string>
            {
                ["key"] = "A",
            }));

            Assert.That(Root.Q<Label>("key-status").text, Does.Contain("A"));
            Assert.That(LastActionContext.SharedBag["inputDriver.keyboard"].ToString(), Does.Contain("InputSystemTestFramework"));
        }

        [UnityTest]
        public IEnumerator PressKeyAction_SupportsExplicitSelector()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new PressKeyAction(), new Dictionary<string, string>
            {
                ["selector"] = "#interaction-input",
                ["key"] = "A",
            }));

            Assert.That(Root.focusController.focusedElement, Is.SameAs(Root.Q<TextField>("interaction-input")));
            Assert.That(Root.Q<Label>("key-status").text, Does.Contain("A"));
        }

        [UnityTest]
        public IEnumerator ExecuteAndValidateCommandActions_DispatchCommandEvents()
        {
            Window.FocusInput();
            yield return null;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ValidateCommandAction(), new Dictionary<string, string>
            {
                ["command"] = "Copy",
            }));

            Assert.That(Root.Q<Label>("key-status").text, Is.EqualTo("Validate: Copy"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ExecuteCommandAction(), new Dictionary<string, string>
            {
                ["command"] = "Paste",
            }));

            Assert.That(Root.Q<Label>("key-status").text, Is.EqualTo("Execute: Paste"));
            Assert.That(LastActionContext.SharedBag["inputDriver.host"].ToString(), Is.EqualTo("OfficialEditorWindowPanelSimulator"));
        }

        [UnityTest]
        public IEnumerator HoverScrollAndDragActions_UpdateInteractionState()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new HoverAction(), new Dictionary<string, string>
            {
                ["selector"] = "#hover-target",
                ["duration"] = "50ms",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ScrollAction(), new Dictionary<string, string>
            {
                ["selector"] = "#scroll-view",
                ["delta"] = "0,120",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new DragAction(), new Dictionary<string, string>
            {
                ["from"] = "10,10",
                ["to"] = "80,80",
                ["duration"] = "64ms",
            }));

            Assert.That(Root.Q<Label>("hover-status").text, Is.EqualTo("Hover: active"));
            Assert.That(Root.Q<ScrollView>("scroll-view").scrollOffset.y, Is.GreaterThan(0f));
            Assert.That(Root.Q<Label>("drag-status").text, Is.EqualTo("Drag: completed"));
        }

        [UnityTest]
        public IEnumerator DragAction_SupportsModifiersAndAlternateButtons()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new DragAction(), new Dictionary<string, string>
            {
                ["from"] = "10,10",
                ["to"] = "120,90",
                ["duration"] = "64ms",
                ["button"] = "right",
                ["modifiers"] = "alt",
            }));

            Assert.That(Root.Q<Label>("drag-status").text, Is.EqualTo("Drag: completed"));
            Assert.That(Root.Q<Label>("pointer-status").text, Does.Contain("button=1"));
            Assert.That(Root.Q<Label>("pointer-status").text, Does.Contain("Alt"));
        }

        [UnityTest]
        public IEnumerator MenuActions_UseOfficialContextAndPopupSimulators()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new OpenContextMenuAction(), new Dictionary<string, string>
            {
                ["selector"] = "#context-menu-target",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertMenuItemAction(), new Dictionary<string, string>
            {
                ["item"] = "Copy",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertMenuItemDisabledAction(), new Dictionary<string, string>
            {
                ["item"] = "Delete",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectContextMenuItemAction(), new Dictionary<string, string>
            {
                ["item"] = "Copy",
            }));

            Assert.That(Root.Q<Label>("menu-status").text, Is.EqualTo("Context: Copy"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new OpenPopupMenuAction(), new Dictionary<string, string>
            {
                ["selector"] = "#popup-menu-dropdown",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectPopupMenuItemAction(), new Dictionary<string, string>
            {
                ["item"] = "Option2",
            }));

            Assert.That(Root.Q<Label>("menu-status").text, Is.EqualTo("Popup: Option2"));
        }

        [UnityTest]
        public IEnumerator ScreenshotManager_CapturesNonPlaceholderPng()
        {
            string screenshotPath = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath));

            Screenshot.CaptureSync(screenshotPath);

            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(File.Exists(screenshotPath), Is.True);
                Assert.That(texture.LoadImage(File.ReadAllBytes(screenshotPath)), Is.True);
                Assert.That(texture.width, Is.GreaterThan(2));
                Assert.That(texture.height, Is.GreaterThan(2));
                Assert.That(Screenshot.LastCaptureSource, Is.Not.Null.And.Not.EqualTo(ScreenshotManager.SourceFallbackTexture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                if (File.Exists(screenshotPath))
                {
                    File.Delete(screenshotPath);
                }
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator TypeTextFastAction_WritesValueDirectly()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new TypeTextFastAction(), new Dictionary<string, string>
            {
                ["selector"] = "#interaction-input",
                ["value"] = "fast",
            }));

            Assert.That(Root.Q<TextField>("interaction-input").value, Is.EqualTo("fast"));
        }

        [UnityTest]
        public IEnumerator PressKeyCombinationAction_SendsShortcut()
        {
            Window.FocusInput();
            yield return null;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new TypeTextAction(), new Dictionary<string, string>
            {
                ["selector"] = "#interaction-input",
                ["value"] = "selectme",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new PressKeyCombinationAction(), new Dictionary<string, string>
            {
                ["keys"] = "Ctrl+A",
            }));

            Assert.That(Root.Q<TextField>("interaction-input").value, Is.EqualTo("selectme"));
        }

        [UnityTest]
        public IEnumerator AssertPropertyAction_ChecksElementProperty()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertPropertyAction(), new Dictionary<string, string>
            {
                ["selector"] = "#interaction-input",
                ["property"] = "maxLength",
                ["expected"] = "-1",
            }));
        }

        [UnityTest]
        public IEnumerator ScreenshotAction_CreatesAttachment()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ScreenshotAction(), new Dictionary<string, string>
            {
                ["name"] = "fixture-screenshot",
            }));

            Assert.That(LastActionContext.CurrentAttachments, Is.Not.Empty);
        }
    }

    public sealed class UnityUIFlowOfficialIntegrationTests
    {
        [Test]
        public void SimulationSession_CapturesInputSystemTextEvents()
        {
            char received = '\0';

            using (var session = new UnityUIFlowSimulationSession())
            {
                session.EnsureInputSystemReady();

                Keyboard.current.onTextInput += OnTextInput;
                try
                {
                    session.SendText("a");
                    session.PressKey(Key.A);
                }
                finally
                {
                    Keyboard.current.onTextInput -= OnTextInput;
                }

                Assert.That(received, Is.EqualTo('a'));
                Assert.That(session.KeyboardDriverName, Does.Contain("InputSystemTestFramework"));
            }

            void OnTextInput(char character)
            {
                received = character;
            }
        }

        [Test]
        public void OfficialUiToolkitAvailability_ScansLoadedAssemblies()
        {
            OfficialUiToolkitTestAvailability availability = OfficialUiToolkitTestAvailability.Detect();

            Assert.That(availability.Describe(), Is.Not.Empty);
            Assert.That(availability.Describe(), Does.Contain("com.unity.ui.test-framework"));
            Assert.That(availability.HasEditorWindowFixture, Is.True);
            Assert.That(availability.HasEditorWindowPanelSimulator, Is.True);
            Assert.That(availability.HasPanelSimulator, Is.True);
            Assert.That(availability.HasConfirmedUiDriver, Is.True);
        }

        [UnityTest]
        public IEnumerator SimulationSession_BindsOfficialEditorWindowHost()
        {
            SampleInteractionWindow window = EditorWindow.GetWindow<SampleInteractionWindow>();
            window.Show();
            yield return null;

            using (var session = new UnityUIFlowSimulationSession())
            {
                bool bound = session.BindEditorWindowHost(window, "EditorWindow.GetWindow<SampleInteractionWindow>()");

                Assert.That(bound, Is.True);
                Assert.That(session.HasExecutableOfficialHost, Is.True);
                Assert.That(session.HasExecutableOfficialPointerDriver, Is.True);
                Assert.That(session.HasExecutableOfficialKeyboardDriver, Is.True);
                Assert.That(session.HostDriverName, Is.EqualTo("OfficialEditorWindowPanelSimulator"));
                Assert.That(session.PointerDriverName, Is.EqualTo("PanelSimulator"));
                Assert.That(session.KeyboardDriverName, Is.EqualTo("PanelSimulator"));
            }

            window.Close();
        }
    }

    public sealed class UnityUIFlowFixtureIntegrationBoundaryTests
    {
        [UnityTest]
        public IEnumerator OfficialEditorWindowHostBridge_ThrowsWhenPanelNull()
        {
            yield return null;
            var window = ScriptableObject.CreateInstance<SampleInteractionWindow>();
            // Do not show the window, so rootVisualElement.panel is null
            Assert.Throws<System.InvalidOperationException>(() => new OfficialEditorWindowHostBridge(window));
            UnityEngine.Object.DestroyImmediate(window);
        }

        [UnityTest]
        public IEnumerator OfficialEditorWindowHostBridge_DisposeIsIdempotent()
        {
            yield return null;
            var window = EditorWindow.GetWindow<SampleInteractionWindow>();
            window.Show();
            yield return null;

            var bridge = new OfficialEditorWindowHostBridge(window);
            Assert.DoesNotThrow(() =>
            {
                bridge.Dispose();
                bridge.Dispose();
            });
            window.Close();
        }

        [UnityTest]
        public IEnumerator OfficialUiMenuDriver_ReturnsFalseWhenMenuNotOpen()
        {
            yield return null;
            var window = EditorWindow.GetWindow<SampleInteractionWindow>();
            window.Show();
            yield return null;

            using (var session = new UnityUIFlowSimulationSession())
            {
                session.BindEditorWindowHost(window, "test");
                var ctx = new ActionContext { Root = window.rootVisualElement };
                Assert.That(session.TrySelectContextMenuItem("DoesNotExist", ctx), Is.False);
                Assert.That(session.TrySelectPopupMenuItem("DoesNotExist", ctx), Is.False);
                Assert.That(session.TryAssertMenuItem("DoesNotExist", true, ctx), Is.False);
            }
            window.Close();
        }

        [Test]
        public void SimulationSession_KeyboardDriverStateTransitions()
        {
            using (var session = new UnityUIFlowSimulationSession())
            {
                session.MarkKeyboardOfficial();
                Assert.That(session.KeyboardDriverName, Is.EqualTo("PanelSimulator"));

                session.MarkKeyboardInputSystem();
                Assert.That(session.KeyboardDriverName, Is.EqualTo("InputSystemTestFramework+UIToolkitBridge"));

                session.MarkKeyboardFallback();
                Assert.That(session.KeyboardDriverName, Is.EqualTo("UIToolkitFallbackOnly"));
            }
        }

        [Test]
        public void SimulationSession_MenuApisReturnFalseWithoutBinding()
        {
            using (var session = new UnityUIFlowSimulationSession())
            {
                var ctx = new ActionContext();
                Assert.That(session.TryOpenContextMenu(null, EventModifiers.None, ctx), Is.False);
                Assert.That(session.TrySelectPopupMenuItem("X", ctx), Is.False);
            }
        }

        [Test]
        public void SimulationSession_DescribeDrivers_ContainsAllSegments()
        {
            using (var session = new UnityUIFlowSimulationSession())
            {
                string description = session.DescribeDrivers();
                Assert.That(description, Does.Contain("host="));
                Assert.That(description, Does.Contain("pointer="));
                Assert.That(description, Does.Contain("keyboard="));
                Assert.That(description, Does.Contain("official="));
            }
        }
    }

    public sealed class UnityUIFlowStrictPointerFixtureTests : UnityUIFlowFixture<SampleInteractionWindow>
    {
        protected override TestOptions CreateDefaultOptions()
        {
            TestOptions options = base.CreateDefaultOptions();
            options.RequireOfficialPointerDriver = true;
            return options;
        }

        [UnityTest]
        public IEnumerator ClickAction_StrictOfficialPointerDriver_UsesOfficialPanelSimulator()
        {
            Task task = ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#click-button",
            });

            yield return UnityUIFlowTestTaskUtility.Await(task);

            Assert.That(Root.Q<Label>("click-status").text, Is.EqualTo("Clicks: 1"));
            Assert.That(LastActionContext.SharedBag["inputDriver.pointer"].ToString(), Is.EqualTo("PanelSimulator"));
        }
    }

    public sealed class UnityUIFlowStrictKeyboardFixtureTests : UnityUIFlowFixture<SampleInteractionWindow>
    {
        protected override TestOptions CreateDefaultOptions()
        {
            TestOptions options = base.CreateDefaultOptions();
            options.RequireInputSystemKeyboardDriver = true;
            return options;
        }

        [UnityTest]
        public IEnumerator TypeTextAction_StrictKeyboardMode_DisablesDirectWriteFallback()
        {
            Task task = ExecuteActionAsync(new TypeTextAction(), new Dictionary<string, string>
            {
                ["selector"] = "#key-status",
                ["value"] = "x",
            });

            yield return UnityUIFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                UnityUIFlowException flowException = ex as UnityUIFlowException;
                Assert.That(flowException, Is.Not.Null);
                Assert.That(flowException.ErrorCode, Is.EqualTo(ErrorCodes.ActionExecutionFailed));
                Assert.That(flowException.Message, Does.Contain("动作 type_text 在高保真模式下禁止回退到直接写值实现"));
            });
        }
    }

    public sealed class UnityUIFlowStrictHostModeTests
    {
        [UnityTest]
        public IEnumerator TestRunner_StrictOfficialHost_FailsFast()
        {
            string yaml = "name: Strict Host\nsteps:\n  - action: wait\n    duration: 16ms\n";
            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(
                yaml,
                new TestOptions
                {
                    Headed = false,
                    RequireOfficialHost = true,
                },
                new VisualElement());

            yield return UnityUIFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                UnityUIFlowException flowException = ex as UnityUIFlowException;
                Assert.That(flowException, Is.Not.Null);
                Assert.That(flowException.ErrorCode, Is.EqualTo(ErrorCodes.FixtureWindowCreateFailed));
                Assert.That(flowException.Message, Does.Contain("瀹樻柟娴嬭瘯瀹夸富"));
            });
        }
    }

    public sealed class UnityUIFlowAdvancedControlsFixtureTests : UnityUIFlowFixture<AdvancedControlsWindow>
    {
        [UnityTest]
        public IEnumerator AdvancedControlActions_UpdateControlState()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#choice-dropdown",
                ["value"] = "Beta",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ToggleFoldoutAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-foldout",
                ["expand"] = "true",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetSliderAction(), new Dictionary<string, string>
            {
                ["selector"] = "#volume-slider",
                ["value"] = "7.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetSliderAction(), new Dictionary<string, string>
            {
                ["selector"] = "#level-slider",
                ["value"] = "4",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetSliderAction(), new Dictionary<string, string>
            {
                ["selector"] = "#range-slider",
                ["min_value"] = "25",
                ["max_value"] = "40",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectListItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#item-list",
                ["index"] = "2",
            }));

            Assert.That(Root.Q<DropdownField>("choice-dropdown").value, Is.EqualTo("Beta"));
            Assert.That(Root.Q<Foldout>("settings-foldout").value, Is.True);
            Assert.That(Root.Q<Slider>("volume-slider").value, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(Root.Q<SliderInt>("level-slider").value, Is.EqualTo(4));
            Assert.That(Root.Q<MinMaxSlider>("range-slider").value.x, Is.EqualTo(25f).Within(0.001f));
            Assert.That(Root.Q<MinMaxSlider>("range-slider").value.y, Is.EqualTo(40f).Within(0.001f));
            Assert.That(Root.Q<ListView>("item-list").selectedIndex, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SetValueAction_SupportsToggleAndSliderTypes()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#enabled-toggle",
                ["value"] = "true",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#range-slider",
                ["value"] = "10,15",
            }));

            Assert.That(Root.Q<Toggle>("enabled-toggle").value, Is.True);
            Assert.That(Root.Q<MinMaxSlider>("range-slider").value.x, Is.EqualTo(10f).Within(0.001f));
            Assert.That(Root.Q<MinMaxSlider>("range-slider").value.y, Is.EqualTo(15f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator ToggleMaskOptionAction_TogglesIndividualBits()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["value"] = "0",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ToggleMaskOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["index"] = "1",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["expected"] = "2",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ToggleMaskOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["value"] = "Two",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["expected"] = "0",
            }));
        }

        [UnityTest]
        public IEnumerator SelectOptionAction_FailsForMissingChoice()
        {
            Task task = ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#choice-dropdown",
                ["value"] = "Missing",
            });

            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.That(task.Exception, Is.Not.Null);
            Assert.That(task.Exception.Flatten().InnerException, Is.TypeOf<UnityUIFlowException>());
            Assert.That(((UnityUIFlowException)task.Exception.Flatten().InnerException).ErrorCode, Is.EqualTo(ErrorCodes.ActionOptionNotFound));
        }

        [UnityTest]
        public IEnumerator SetValueAndAssertValue_SupportComplexFieldTypes()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quick-popup",
                ["value"] = "South",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quick-popup",
                ["expected"] = "South",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#mode-enum",
                ["value"] = "Advanced",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#mode-enum",
                ["expected"] = "Advanced",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#permissions-flags",
                ["value"] = "Read, Execute",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#permissions-flags",
                ["expected"] = "Read, Execute",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#permissions-flags",
                ["indices"] = "1,3",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#permissions-flags",
                ["expected"] = "Read, Execute",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["indices"] = "0,2",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#feature-mask",
                ["expected"] = "5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#tag-selector",
                ["value"] = "Untagged",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#tag-selector",
                ["expected"] = "Untagged",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectOptionAction(), new Dictionary<string, string>
            {
                ["selector"] = "#layer-selector",
                ["index"] = "0",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#layer-selector",
                ["expected"] = "0",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#template-asset",
                ["value"] = "Assets/Examples/Uxml/SampleInteractionWindow.uxml",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#template-asset",
                ["expected"] = "Assets/Examples/Uxml/SampleInteractionWindow.uxml",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#speed-curve",
                ["value"] = "0:0:1:1;1:2:0:0",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#speed-curve",
                ["expected"] = "0:0:1:1;1:2:0:0",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#ramp-gradient",
                ["value"] = "0:#FF0000FF;1:#00FF00FF|0:1;1:0.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#ramp-gradient",
                ["expected"] = "0:#FF0000FF;1:#00FF00FF|0:1;1:0.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#max-items",
                ["value"] = "42",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#max-items",
                ["expected"] = "42",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#total-bytes",
                ["value"] = "4200",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#total-bytes",
                ["expected"] = "4200",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#accent-color",
                ["value"] = "#FF8040CC",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#accent-color",
                ["expected"] = "#FF8040CC",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#anchor-vector2",
                ["value"] = "9.5,8.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#anchor-vector2",
                ["expected"] = "9.5,8.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#cell-vector2int",
                ["value"] = "7,6",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#cell-vector2int",
                ["expected"] = "7,6",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#offset-vector3",
                ["value"] = "1.5,2.5,3.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#offset-vector3",
                ["expected"] = "1.5,2.5,3.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quaternion-vector4",
                ["value"] = "1,2,3,4",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quaternion-vector4",
                ["expected"] = "1,2,3,4",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#viewport-rect",
                ["value"] = "5,6,120,80",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#viewport-rect",
                ["expected"] = "5,6,120,80",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#grid-rectint",
                ["value"] = "2,3,40,50",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#grid-rectint",
                ["expected"] = "2,3,40,50",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#volume-bounds",
                ["value"] = "1,2,3,4,5,6",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#volume-bounds",
                ["expected"] = "1,2,3,4,5,6",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#voxel-boundsint",
                ["value"] = "3,4,5,6,7,8",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#voxel-boundsint",
                ["expected"] = "3,4,5,6,7,8",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#content-hash",
                ["value"] = "11111111222222223333333344444444",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#content-hash",
                ["expected"] = "11111111222222223333333344444444",
            }));

            Color actualColor = Root.Q<ColorField>("accent-color").value;
            Assert.That(Root.Q<PopupField<string>>("quick-popup").value, Is.EqualTo("South"));
            Assert.That(Root.Q<EnumField>("mode-enum").value.ToString(), Is.EqualTo("Advanced"));
            Assert.That(Root.Q<EnumFlagsField>("permissions-flags").value.ToString(), Is.EqualTo("Read, Execute"));
            Assert.That(Root.Q<MaskField>("feature-mask").value, Is.EqualTo(5));
            Assert.That(Root.Q<TagField>("tag-selector").value, Is.EqualTo("Untagged"));
            Assert.That(Root.Q<LayerField>("layer-selector").value, Is.EqualTo(0));
            Assert.That(AssetDatabase.GetAssetPath(Root.Q<ObjectField>("template-asset").value), Is.EqualTo("Assets/Examples/Uxml/SampleInteractionWindow.uxml"));
            Assert.That(Root.Q<CurveField>("speed-curve").value.keys, Has.Length.EqualTo(2));
            Assert.That(Root.Q<CurveField>("speed-curve").value.keys[1].value, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(Root.Q<GradientField>("ramp-gradient").value.colorKeys[1].color.g, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(Root.Q<GradientField>("ramp-gradient").value.alphaKeys[1].alpha, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(Root.Q<UnsignedIntegerField>("max-items").value, Is.EqualTo(42u));
            Assert.That(Root.Q<UnsignedLongField>("total-bytes").value, Is.EqualTo(4200ul));
            Assert.That(actualColor.r, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(actualColor.g, Is.EqualTo(128f / 255f).Within(0.0001f));
            Assert.That(actualColor.b, Is.EqualTo(64f / 255f).Within(0.0001f));
            Assert.That(actualColor.a, Is.EqualTo(204f / 255f).Within(0.0001f));
            Assert.That(Root.Q<Vector2Field>("anchor-vector2").value, Is.EqualTo(new Vector2(9.5f, 8.5f)));
            Assert.That(Root.Q<Vector2IntField>("cell-vector2int").value, Is.EqualTo(new Vector2Int(7, 6)));
            Assert.That(Root.Q<Vector3Field>("offset-vector3").value, Is.EqualTo(new Vector3(1.5f, 2.5f, 3.5f)));
            Assert.That(Root.Q<Vector4Field>("quaternion-vector4").value, Is.EqualTo(new Vector4(1f, 2f, 3f, 4f)));
            Assert.That(Root.Q<RectField>("viewport-rect").value, Is.EqualTo(new Rect(5f, 6f, 120f, 80f)));
            Assert.That(Root.Q<RectIntField>("grid-rectint").value, Is.EqualTo(new RectInt(2, 3, 40, 50)));
            Assert.That(Root.Q<BoundsField>("volume-bounds").value.center, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Root.Q<BoundsField>("volume-bounds").value.extents, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            Assert.That(Root.Q<BoundsIntField>("voxel-boundsint").value.position, Is.EqualTo(new Vector3Int(3, 4, 5)));
            Assert.That(Root.Q<BoundsIntField>("voxel-boundsint").value.size, Is.EqualTo(new Vector3Int(6, 7, 8)));
            Assert.That(Root.Q<Hash128Field>("content-hash").value.ToString(), Is.EqualTo("11111111222222223333333344444444"));
        }

        [UnityTest]
        public IEnumerator SortAndResizeColumnActions_UpdateMultiColumnViews()
        {
            VisualElement multiColumnListView = Root.Q<VisualElement>("planet-list");
            VisualElement multiColumnTreeView = Root.Q<VisualElement>("scene-tree");
            Assert.That(multiColumnListView, Is.Not.Null);
            Assert.That(multiColumnTreeView, Is.Not.Null);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SortColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#planet-list",
                ["column"] = "planet",
                ["direction"] = "descending",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ResizeColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#scene-tree",
                ["index"] = "0",
                ["width"] = "240",
            }));

            object firstSortDescription = GetIndexedMember(ReadMember(multiColumnListView, "sortColumnDescriptions"), 0);
            object firstTreeColumn = GetIndexedMember(ReadMember(multiColumnTreeView, "columns"), 0);

            Assert.That(ReadMember(firstSortDescription, "columnName", "ColumnName")?.ToString(), Is.EqualTo("planet"));
            Assert.That(ReadMember(firstSortDescription, "direction", "Direction")?.ToString(), Is.EqualTo("Descending"));
            Assert.That(ReadMember(firstTreeColumn, "width", "Width")?.ToString(), Does.Contain("240"));
        }

        [UnityTest]
        public IEnumerator ToolbarInheritedControls_And_MenuAutomation_Work()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-run",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-live-toggle",
                ["value"] = "true",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-search",
                ["value"] = "ship",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new OpenPopupMenuAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-menu",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectPopupMenuItemAction(), new Dictionary<string, string>
            {
                ["item"] = "Reset",
            }));

            Assert.That(Root.Q<ToolbarToggle>("toolbar-live-toggle").value, Is.True);
            Assert.That(Root.Q<ToolbarSearchField>("toolbar-search").value, Is.EqualTo("ship"));
            Assert.That(Root.Q<Label>("toolbar-status").text, Is.EqualTo("Toolbar: reset"));
        }

        [UnityTest]
        public IEnumerator PropertyFieldAndInspectorElement_AllowAutomationViaGeneratedChildren()
        {
            yield return null;

            Assert.That(Root.Q<PropertyField>("property-title-field"), Is.Not.Null);
            Assert.That(Root.Q<VisualElement>("settings-inspector"), Is.Not.Null);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#property-title-input",
                ["value"] = "Panel Name",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#inspector-count-input",
                ["value"] = "33",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#inspector-enabled-toggle",
                ["value"] = "false",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#property-title-input",
                ["expected"] = "Panel Name",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#inspector-count-input",
                ["expected"] = "33",
            }));

            Assert.That(Root.Q<TextField>("property-title-input").value, Is.EqualTo("Panel Name"));
            Assert.That(Root.Q<IntegerField>("inspector-count-input").value, Is.EqualTo(33));
            Assert.That(Root.Q<Toggle>("inspector-enabled-toggle").value, Is.False);
        }

        [UnityTest]
        public IEnumerator BoundValueActions_ProvideHigherLevelPropertyAndInspectorAutomation()
        {
            yield return null;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#property-title-field",
                ["property"] = "inspectorTitle",
                ["value"] = "Semantic Title",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-inspector",
                ["property"] = "inspectorCount",
                ["value"] = "64",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#property-title-field",
                ["property"] = "inspectorTitle",
                ["expected"] = "Semantic Title",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-inspector",
                ["property"] = "inspectorCount",
                ["expected"] = "64",
            }));

            Assert.That(Root.Q<TextField>("property-title-input").value, Is.EqualTo("Semantic Title"));
            Assert.That(Root.Q<IntegerField>("inspector-count-input").value, Is.EqualTo(64));
        }

        [UnityTest]
        public IEnumerator ToolbarPopupSearchAndBreadcrumbs_ArePartiallyAutomatable()
        {
            yield return null;

            VisualElement popupSearch = Root.Q<VisualElement>("toolbar-popup-search");
            VisualElement breadcrumbs = Root.Q<VisualElement>("toolbar-breadcrumbs");
            if (popupSearch == null || breadcrumbs == null)
            {
                Assert.Ignore("Current Unity environment does not expose ToolbarPopupSearchField or ToolbarBreadcrumbs.");
            }

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-popup-search",
                ["value"] = "asset",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ClickAction(), new Dictionary<string, string>
            {
                ["selector"] = "#breadcrumb-item-1",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-popup-search",
                ["expected"] = "asset",
            }));

            Assert.That(ReadMember(popupSearch, "value", "Value")?.ToString(), Is.EqualTo("asset"));
            Assert.That(Root.Q<Label>("toolbar-status").text, Is.EqualTo("Toolbar: breadcrumb-settings"));
        }

        [UnityTest]
        public IEnumerator NavigateBreadcrumbAndUnifiedMenuActions_Work()
        {
            yield return null;

            VisualElement breadcrumbs = Root.Q<VisualElement>("toolbar-breadcrumbs");
            if (breadcrumbs == null)
            {
                Assert.Ignore("Current Unity environment does not expose ToolbarBreadcrumbs.");
            }

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new NavigateBreadcrumbAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-breadcrumbs",
                ["label"] = "Settings",
            }));

            Assert.That(Root.Q<Label>("toolbar-status").text, Is.EqualTo("Toolbar: breadcrumb-settings"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new MenuItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-menu",
                ["kind"] = "popup",
                ["mode"] = "select",
                ["item"] = "Reset",
            }));

            Assert.That(Root.Q<Label>("toolbar-status").text, Is.EqualTo("Toolbar: reset"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new MenuItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#context-menu-target",
                ["kind"] = "context",
                ["mode"] = "assert_disabled",
                ["item"] = "Delete",
            }));
        }

        [UnityTest]
        public IEnumerator TreeAndTabActions_UpdateNavigationState()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectTreeItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#navigation-tree",
                ["id"] = "120",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectTabAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-tabs",
                ["label"] = "About",
            }));

            Assert.That(Root.Q<Label>("tree-selection-status").text, Is.EqualTo("Tree: Leaf A2"));
            Assert.That(Root.Q<Label>("tab-selection-status").text, Is.EqualTo("Tab: About"));
        }

        [UnityTest]
        public IEnumerator SelectListItemAction_SupportsIndicesParameterForMultiSelect()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectListItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#item-list",
                ["indices"] = "1,3",
            }));

            ListView listView = Root.Q<ListView>("item-list");
            PropertyInfo selectedIndicesProperty = listView.GetType().GetProperty("selectedIndices");
            Assert.That(selectedIndicesProperty, Is.Not.Null);

            var selected = new List<int>();
            foreach (object value in (System.Collections.IEnumerable)selectedIndicesProperty.GetValue(listView))
            {
                selected.Add((int)value);
            }

            Assert.That(selected, Is.EquivalentTo(new[] { 1, 3 }));
        }

        [UnityTest]
        public IEnumerator DragReorderAction_ReordersListViewItems()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new DragReorderAction(), new Dictionary<string, string>
            {
                ["selector"] = "#item-list",
                ["from_index"] = "1",
                ["to_index"] = "3",
            }));

            IList items = Root.Q<ListView>("item-list").itemsSource;
            Assert.That(items[0], Is.EqualTo("Alpha"));
            Assert.That(items[1], Is.EqualTo("Gamma"));
            Assert.That(items[2], Is.EqualTo("Delta"));
            Assert.That(items[3], Is.EqualTo("Beta"));
        }

        [UnityTest]
        public IEnumerator MultiColumnCollectionActions_ReuseCollectionSelectionPipeline()
        {
            VisualElement multiColumnListView = Root.Q<VisualElement>("planet-list");
            VisualElement multiColumnTreeView = Root.Q<VisualElement>("scene-tree");
            Assert.That(multiColumnListView, Is.Not.Null, "Current Unity environment did not create MultiColumnListView.");
            Assert.That(multiColumnTreeView, Is.Not.Null, "Current Unity environment did not create MultiColumnTreeView.");

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectListItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#planet-list",
                ["indices"] = "1,3",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectTreeItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#scene-tree",
                ["id"] = "120",
            }));

            Assert.That(ReadIndicesProperty(multiColumnListView, "selectedIndices"), Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(ReadIntProperty(multiColumnTreeView, "selectedIndex"), Is.GreaterThanOrEqualTo(0));
            Assert.That(ReadIntProperty(multiColumnTreeView, "selectedId", "selectedItemId"), Is.EqualTo(120));
        }

        [UnityTest]
        public IEnumerator ScrollerAndSplitView_Actions_UpdateState()
        {
            yield return null;

            Scroller scroller = Root.Q<Scroller>("standalone-scroller");
            Assert.That(scroller, Is.Not.Null);

            float initialLeftWidth = Root.Q<VisualElement>("left-pane").worldBound.width;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["value"] = "45",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new AssertValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["expected"] = "45",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new DragAction(), new Dictionary<string, string>
            {
                ["from"] = "#workspace-split .unity-two-pane-split-view__dragline-anchor",
                ["to"] = "260,40",
                ["duration"] = "64ms",
            }));

            yield return null;

            Assert.That(scroller.value, Is.EqualTo(45f).Within(0.001f));
            Assert.That(Root.Q<Label>("scroller-status").text, Does.Contain("45"));
            Assert.That(Mathf.Abs(Root.Q<VisualElement>("left-pane").worldBound.width - initialLeftWidth), Is.GreaterThan(0.5f));
        }

        [UnityTest]
        public IEnumerator PageScrollerAndSetSplitViewSizeActions_Work()
        {
            yield return null;

            Scroller scroller = Root.Q<Scroller>("standalone-scroller");
            VisualElement splitView = Root.Q<VisualElement>("workspace-split");
            Assert.That(scroller, Is.Not.Null);
            Assert.That(splitView, Is.Not.Null);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new PageScrollerAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["direction"] = "down",
                ["pages"] = "2",
                ["page_size"] = "7.5",
            }));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetSplitViewSizeAction(), new Dictionary<string, string>
            {
                ["selector"] = "#workspace-split",
                ["pane"] = "0",
                ["size"] = "220",
            }));

            yield return null;

            Assert.That(scroller.value, Is.EqualTo(25f).Within(0.001f));
            Assert.That(Root.Q<Label>("scroller-status").text, Does.Contain("25"));
            Assert.That(Root.Q<VisualElement>("left-pane").resolvedStyle.width, Is.EqualTo(220f).Within(1.5f));
        }

        [UnityTest]
        public IEnumerator ObjectField_SupportsAdditionalSearchStrategies()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#template-asset",
                ["value"] = "name:SampleInteractionWindow",
            }));

            Assert.That(AssetDatabase.GetAssetPath(Root.Q<ObjectField>("template-asset").value), Is.EqualTo("Assets/Examples/Uxml/SampleInteractionWindow.uxml"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SetValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#template-asset",
                ["value"] = "search:VisualTreeAsset:SampleInteractionWindow",
            }));

            Assert.That(AssetDatabase.GetAssetPath(Root.Q<ObjectField>("template-asset").value), Is.EqualTo("Assets/Examples/Uxml/SampleInteractionWindow.uxml"));
        }

        [UnityTest]
        public IEnumerator CloseTabAction_ClosesSpecifiedTab()
        {
            yield return null;
            TabView tabView = Root.Q<TabView>("settings-tabs");
            Assert.That(tabView, Is.Not.Null);
            int initialTabCount = tabView.contentContainer.childCount;
            Assert.That(initialTabCount, Is.EqualTo(3));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new CloseTabAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-tabs",
                ["label"] = "About",
            }));

            Assert.That(tabView.contentContainer.childCount, Is.EqualTo(initialTabCount - 1));
        }

        [UnityTest]
        public IEnumerator DragScrollerAction_UpdatesScrollerValue()
        {
            Scroller scroller = Root.Q<Scroller>("standalone-scroller");
            Assert.That(scroller, Is.Not.Null);
            float before = scroller.value;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new DragScrollerAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["ratio"] = "0.5",
                ["duration"] = "64ms",
            }));

            Assert.That(Mathf.Abs(scroller.value - before), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator SelectTreeItemAction_SupportsLabelSelection()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectTreeItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#navigation-tree",
                ["label"] = "Leaf A2",
            }));

            Assert.That(Root.Q<Label>("tree-selection-status").text, Is.EqualTo("Tree: Leaf A2"));
        }

        [UnityTest]
        public IEnumerator SelectListItemAction_SupportsSingleIndexSelection()
        {
            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SelectListItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#item-list",
                ["index"] = "2",
            }));

            Assert.That(Root.Q<Label>("list-selection-status").text, Is.EqualTo("List: 2"));
        }

        [UnityTest]
        public IEnumerator SortColumnAction_SupportsDefaultMode()
        {
            VisualElement multiColumnListView = Root.Q<VisualElement>("planet-list");
            Assert.That(multiColumnListView, Is.Not.Null);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new SortColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#planet-list",
                ["column"] = "planet",
                ["direction"] = "ascending",
            }));

            object firstSortDescription = GetIndexedMember(ReadMember(multiColumnListView, "sortColumnDescriptions"), 0);
            Assert.That(ReadMember(firstSortDescription, "columnName", "ColumnName")?.ToString(), Is.EqualTo("planet"));
            Assert.That(ReadMember(firstSortDescription, "direction", "Direction")?.ToString(), Is.EqualTo("Ascending"));
        }

        [UnityTest]
        public IEnumerator ResizeColumnAction_SupportsDifferentIndices()
        {
            VisualElement multiColumnTreeView = Root.Q<VisualElement>("scene-tree");
            Assert.That(multiColumnTreeView, Is.Not.Null);

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ResizeColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#scene-tree",
                ["index"] = "0",
                ["width"] = "200",
            }));

            object firstTreeColumn = GetIndexedMember(ReadMember(multiColumnTreeView, "columns"), 0);
            Assert.That(ReadMember(firstTreeColumn, "width", "Width")?.ToString(), Does.Contain("200"));
        }

        [UnityTest]
        public IEnumerator ReadBreadcrumbsAction_PopulatesSharedBag()
        {
            VisualElement breadcrumbs = Root.Q<VisualElement>("toolbar-breadcrumbs");
            if (breadcrumbs == null)
            {
                Assert.Ignore("Current Unity environment does not expose ToolbarBreadcrumbs.");
            }

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ReadBreadcrumbsAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-breadcrumbs",
                ["bag_key"] = "crumbs",
            }));

            Assert.That(LastActionContext.SharedBag.ContainsKey("crumbs"), Is.True);
            var crumbs = LastActionContext.SharedBag["crumbs"] as System.Collections.Generic.List<string>;
            Assert.That(crumbs, Is.Not.Null);
            Assert.That(crumbs.Count, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator ClickPopupItemAction_UpdatesPopupFieldValue()
        {
            yield return null;
            PopupField<string> popup = Root.Q<PopupField<string>>("quick-popup");
            Assert.That(popup, Is.Not.Null);
            Assert.That(popup.value, Is.EqualTo("North"));

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new ClickPopupItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quick-popup",
                ["value"] = "South",
            }));

            Assert.That(popup.value, Is.EqualTo("South"));
        }

        [UnityTest]
        public IEnumerator NavigateBreadcrumbAction_SupportsIndexNavigation()
        {
            VisualElement breadcrumbs = Root.Q<VisualElement>("toolbar-breadcrumbs");
            if (breadcrumbs == null)
            {
                Assert.Ignore("Current Unity environment does not expose ToolbarBreadcrumbs.");
            }

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteActionAsync(new NavigateBreadcrumbAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-breadcrumbs",
                ["index"] = "1",
            }));

            Assert.That(Root.Q<Label>("toolbar-status").text, Is.EqualTo("Toolbar: breadcrumb-settings"));
        }

        private static List<int> ReadIndicesProperty(object target, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                PropertyInfo property = target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(target) is System.Collections.IEnumerable enumerable)
                {
                    var values = new List<int>();
                    foreach (object entry in enumerable)
                    {
                        values.Add((int)entry);
                    }

                    return values;
                }
            }

            return new List<int>();
        }

        private static int ReadIntProperty(object target, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                PropertyInfo property = target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(target) is int value)
                {
                    return value;
                }
            }

            return -1;
        }

        private static object ReadMember(object target, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                {
                    return property.GetValue(target);
                }

                FieldInfo field = target?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return field.GetValue(target);
                }
            }

            return null;
        }

        private static object GetIndexedMember(object collection, int index)
        {
            if (collection == null)
            {
                return null;
            }

            PropertyInfo indexer = collection.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (indexer != null)
            {
                return indexer.GetValue(collection, new object[] { index });
            }

            int current = 0;
            foreach (object item in (System.Collections.IEnumerable)collection)
            {
                if (current++ == index)
                {
                    return item;
                }
            }

            return null;
        }

        [UnityTest]
        public IEnumerator FloatingPanelLocator_CanFindElementsInFloatingPanels()
        {
            yield return null;
            Assert.That(FloatingPanelLocator.IsAvailable, Is.True);

            // Ensure the floating panel enumerator returns without throwing
            int count = 0;
            foreach (VisualElement root in FloatingPanelLocator.GetFloatingPanelRoots(Root))
            {
                count++;
            }
            Assert.That(count, Is.GreaterThanOrEqualTo(0));
        }

        [UnityTest]
        public IEnumerator Finder_MatchesFirstChildPseudoOnRealDom()
        {
            yield return null;
            var compiler = new SelectorCompiler();
            VisualElement first = Finder.Find(compiler.Compile("#menu-bar > .item:first-child"), Root).Element;
            Assert.That(first, Is.Not.Null);
            Assert.That(first.name, Is.EqualTo("menu-item-1"));
        }

        [UnityTest]
        public IEnumerator Finder_MatchesDataAttributeFromUserData()
        {
            yield return null;
            Root.Q<Button>("login-button").userData = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["data-role"] = "primary-login",
            };
            yield return null;

            VisualElement found = Finder.Find(new SelectorCompiler().Compile("[data-role=primary-login]"), Root, false).Element;
            Assert.That(found, Is.Not.Null);
            Assert.That(found.name, Is.EqualTo("login-button"));
        }

        [UnityTest]
        public IEnumerator Finder_ExistsAndVisibilityBoundaries()
        {
            yield return null;
            var hidden = new VisualElement { name = "display-none-test" };
            hidden.style.display = DisplayStyle.None;
            Root.Add(hidden);

            var opacityZero = new VisualElement { name = "opacity-zero-test" };
            opacityZero.style.opacity = 0;
            Root.Add(opacityZero);

            yield return null;

            Assert.That(Finder.Exists(new SelectorCompiler().Compile("#display-none-test"), Root, false), Is.True);
            Assert.That(Finder.Exists(new SelectorCompiler().Compile("#display-none-test"), Root, true), Is.False);
            Assert.That(Finder.Exists(new SelectorCompiler().Compile("#opacity-zero-test"), Root, false), Is.True);
            Assert.That(Finder.Exists(new SelectorCompiler().Compile("#opacity-zero-test"), Root, true), Is.False);
            Assert.That(Finder.Exists(new SelectorCompiler().Compile("#not-in-tree"), Root, false), Is.False);

            Root.Remove(hidden);
            Root.Remove(opacityZero);
        }

        [UnityTest]
        public IEnumerator ScreenshotManager_CapturesFallbackWhenWindowNotFocused()
        {
            yield return null;
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var options = new TestOptions { ScreenshotPath = tempDir };
            var manager = new ScreenshotManager(options, () => Window);
            string path = manager.CaptureSync(Path.Combine(tempDir, "test.png"));

            Assert.That(File.Exists(path), Is.True);
            Assert.That(manager.LastCaptureSource, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator FallbackUiPointerDriver_ClickAndScroll_Work()
        {
            yield return null;
            var driver = new FallbackUiPointerDriver();
            var context = new ActionContext { Root = Root, CancellationToken = System.Threading.CancellationToken.None };

            Label status = Root.Q<Label>("interaction-status");
            if (status == null)
            {
                Assert.Pass("Window does not contain interaction-status, skipping");
            }

            VisualElement target = Root.Q<Button>("interaction-button") ?? Root.Q<Button>("login-button");
            driver.Click(target, 1, UiMouseButton.LeftMouse, EventModifiers.None, context);
            yield return null;
            Assert.That(driver.DriverName, Is.EqualTo("UIToolkitFallbackOnly"));
        }

        [UnityTest]
        public IEnumerator AssertMenuItemActions_FailForMissingItem()
        {
            yield return null;
            var openAction = new OpenContextMenuAction();
            var assertAction = new AssertMenuItemAction();

            // Missing menu item should throw timeout/element wait error when no menu is open
            Task assertTask = ExecuteActionAsync(assertAction, new Dictionary<string, string>
            {
                ["item"] = "NonExistentItem",
            });

            yield return UnityUIFlowTestTaskUtility.AwaitFailure(assertTask, ex =>
            {
                Assert.That(ex, Is.TypeOf<UnityUIFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator DragAction_WithInvalidCoordinates_Throws()
        {
            yield return null;
            var action = new DragAction();
            Task task = ExecuteActionAsync(action, new Dictionary<string, string>
            {
                ["from"] = "abc",
                ["to"] = "1,2",
            });

            yield return UnityUIFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UnityUIFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator WaitAction_BoundaryDurations()
        {
            yield return null;
            var action = new WaitAction();
            Task task = ExecuteActionAsync(action, new Dictionary<string, string>
            {
                ["duration"] = "0ms",
            });
            yield return UnityUIFlowTestTaskUtility.Await(task);

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() =>
            {
                var a = new WaitAction();
                var t = a.ExecuteAsync(Root, new ActionContext { Root = Root, CancellationToken = System.Threading.CancellationToken.None }, new Dictionary<string, string> { ["duration"] = "601s" });
            });
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.DurationLiteralInvalid));
        }

        [UnityTest]
        public IEnumerator TypeTextAction_OnNonInputElement_ThrowsOrFails()
        {
            yield return null;
            var action = new TypeTextAction();
            Task task = ExecuteActionAsync(action, new Dictionary<string, string>
            {
                ["selector"] = "#status-label",
                ["value"] = "should fail",
            });
            yield return UnityUIFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UnityUIFlowException>().Or.TypeOf<System.Exception>());
            });
        }
    }
}
