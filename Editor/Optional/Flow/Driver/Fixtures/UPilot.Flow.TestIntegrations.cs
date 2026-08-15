using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Internal;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using UnityEditor.UIElements.TestFramework;
using UiMouseButton = UnityEngine.UIElements.MouseButton;

namespace CodingRiver.UPilot.Flow
{
    public sealed class OfficialUiToolkitTestAvailability
    {
        public string EditorWindowFixtureTypeName { get; private set; }

        public string EditorWindowPanelSimulatorTypeName { get; private set; }

        public string PanelSimulatorTypeName { get; private set; }

        public bool HasEditorWindowFixture => !string.IsNullOrWhiteSpace(EditorWindowFixtureTypeName);

        public bool HasEditorWindowPanelSimulator => !string.IsNullOrWhiteSpace(EditorWindowPanelSimulatorTypeName);

        public bool HasPanelSimulator => !string.IsNullOrWhiteSpace(PanelSimulatorTypeName);

        public bool HasConfirmedUiDriver => HasEditorWindowPanelSimulator && HasPanelSimulator;

        public string Describe()
        {
            if (HasConfirmedUiDriver)
            {
                return $"{EditorWindowFixtureTypeName} + {EditorWindowPanelSimulatorTypeName} + {PanelSimulatorTypeName} (available via com.unity.ui.test-framework)";
            }

            if (HasEditorWindowFixture && HasEditorWindowPanelSimulator)
            {
                return $"{EditorWindowFixtureTypeName} + {EditorWindowPanelSimulatorTypeName} (missing PanelSimulator)";
            }

            if (HasEditorWindowFixture)
            {
                return $"{EditorWindowFixtureTypeName} (missing EditorWindowPanelSimulator / PanelSimulator)";
            }

            if (HasEditorWindowPanelSimulator)
            {
                return $"{EditorWindowPanelSimulatorTypeName} (missing EditorWindowUITestFixture / PanelSimulator)";
            }

            if (HasPanelSimulator)
            {
                return $"{PanelSimulatorTypeName} (missing EditorWindowUITestFixture / EditorWindowPanelSimulator)";
            }

            return "unavailable (EditorWindowUITestFixture, EditorWindowPanelSimulator, and PanelSimulator not found)";
        }

        public static OfficialUiToolkitTestAvailability Detect()
        {
            return new OfficialUiToolkitTestAvailability
            {
                EditorWindowFixtureTypeName = typeof(EditorWindowUITestFixture<>).FullName,
                EditorWindowPanelSimulatorTypeName = typeof(EditorWindowPanelSimulator).FullName,
                PanelSimulatorTypeName = typeof(PanelSimulator).FullName,
            };
        }
    }

    public sealed class OfficialEditorWindowHostBridge : IDisposable
    {
        private EditorWindowPanelSimulator _simulator;

        public OfficialEditorWindowHostBridge(EditorWindow window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            if (window.rootVisualElement == null || window.rootVisualElement.panel == null)
            {
                throw new InvalidOperationException($"Window {window.GetType().Name} does not have a live UI Toolkit panel.");
            }

            Window = window;
            try
            {
                _simulator = new EditorWindowPanelSimulator(window);
                _simulator.FrameUpdate();
                Codingriver.Logger.Log($"[UPilot Flow] 初始化官方测试宿主桥接 {window.GetType().Name} 成功");
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogError($"[UPilot Flow] 初始化官方测试宿主桥接失败: {ex.Message}");
                throw;
            }
        }

        public EditorWindow Window { get; }

        public PanelSimulator Simulator => _simulator;

        public string HostDriverName => "OfficialEditorWindowPanelSimulator";

        public void Dispose()
        {
            if (_simulator == null)
            {
                return;
            }

            try
            {
                _simulator.SetWindow(null);
            }
            finally
            {
                _simulator = null;
            }
        }
    }

    public sealed class UPilotFlowMenuBridgeFixture : UITestFixture
    {
        public UPilotFlowMenuBridgeFixture(PanelSimulator simulator)
            : base(FixtureType.Editor)
        {
            if (simulator == null)
            {
                throw new ArgumentNullException(nameof(simulator));
            }

            clearContentAfterTest = false;
            simulate = simulator;
        }
    }

    public sealed class OfficialUiMenuDriver : IDisposable
    {
        private static readonly MethodInfo BeforeTestMethod = typeof(UITestComponent).GetMethod("DoBeforeTest", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo AfterTestMethod = typeof(UITestComponent).GetMethod("DoAfterTest", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly PanelSimulator _simulator;
        private readonly UPilotFlowMenuBridgeFixture _fixture;
        private ContextMenuSimulator _contextMenuSimulator;
        private PopupMenuSimulator _popupMenuSimulator;

        public OfficialUiMenuDriver(PanelSimulator simulator)
        {
            _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
            _fixture = new UPilotFlowMenuBridgeFixture(simulator);
            _fixture.FixtureOneTimeSetUp();
            _fixture.FixtureSetUp();
            _contextMenuSimulator = CreateComponent<ContextMenuSimulator>();
            _popupMenuSimulator = CreateComponent<PopupMenuSimulator>();
            _fixture.AddTestComponent(_contextMenuSimulator);
            _fixture.AddTestComponent(_popupMenuSimulator);
            _simulator.FrameUpdate();
        }

        public bool OpenContextMenu(VisualElement element, EventModifiers modifiers)
        {
            if (element == null)
            {
                return false;
            }

            DiscardMenus();
            _simulator.Click(element, UiMouseButton.RightMouse, modifiers);
            _simulator.FrameUpdate();
            if (!_contextMenuSimulator.menuIsDisplayed)
            {
                TryPopulateContextMenuDirectly(element, modifiers);
                _simulator.FrameUpdate();
            }
            Codingriver.Logger.Log($"[UPilot Flow] context menu probe displayed={_contextMenuSimulator.menuIsDisplayed} items={_contextMenuSimulator.menuItemCount} target={element.name}");
            return _contextMenuSimulator.menuIsDisplayed;
        }

        private bool TryPopulateContextMenuDirectly(VisualElement element, EventModifiers modifiers)
        {
            try
            {
                MethodInfo managerGetter = typeof(ContextMenuSimulator).GetMethod(
                    "GetContextualMenuManager",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                ContextualMenuManager manager = managerGetter?.Invoke(null, new object[] { element.panel }) as ContextualMenuManager;
                if (manager == null)
                    return false;

                var triggerSource = new UnityEngine.Event
                {
                    type = EventType.ContextClick,
                    mousePosition = element.worldBound.center,
                    button = (int)UiMouseButton.RightMouse,
                    modifiers = modifiers,
                };
                var menu = new DropdownMenu();
                using (MouseUpEvent trigger = MouseUpEvent.GetPooled(triggerSource))
                using (ContextualMenuPopulateEvent populate = ContextualMenuPopulateEvent.GetPooled(trigger, menu, element, manager))
                {
                    populate.target = element;
                    element.SendEvent(populate);
                }

                return _contextMenuSimulator.menuIsDisplayed;
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] direct context menu population failed: {ex.Message}");
                return false;
            }
        }

        public bool SelectContextMenuItem(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
            {
                return false;
            }

            bool selected = false;
            try
            {
                if (_contextMenuSimulator.menuIsDisplayed)
                {
                    foreach (string candidate in ActionHelpers.MenuItemNameCandidates(itemName))
                    {
                        if (_contextMenuSimulator.SimulateMenuSelection(candidate))
                        {
                            selected = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (_contextMenuSimulator.menuIsDisplayed)
                {
                    _contextMenuSimulator.DiscardMenu();
                }
            }

            return selected;
        }

        public bool OpenPopupMenu(VisualElement element, EventModifiers modifiers)
        {
            VisualElement trigger = ResolvePopupTrigger(element);
            if (trigger == null)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] 打开弹出菜单失败: trigger 为空");
                return false;
            }

            DiscardMenus();
            _simulator.Click(trigger, UiMouseButton.LeftMouse, modifiers);
            _simulator.FrameUpdate();
            bool displayed = _popupMenuSimulator.menuIsDisplayed;
            if (displayed)
            {
                Codingriver.Logger.Log($"[UPilot Flow] 打开弹出菜单成功: trigger={trigger.name} ({trigger.GetType().Name})");
            }
            else
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] 打开弹出菜单后菜单未显示: trigger={trigger.name}");
            }
            return displayed;
        }

        public bool SelectPopupMenuItem(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
            {
                return false;
            }

            bool selected = false;
            try
            {
                if (_popupMenuSimulator.menuIsDisplayed)
                {
                    foreach (string candidate in ActionHelpers.MenuItemNameCandidates(itemName))
                    {
                        if (_popupMenuSimulator.SimulateMenuSelection(candidate))
                        {
                            selected = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (_popupMenuSimulator.menuIsDisplayed)
                {
                    _popupMenuSimulator.DiscardMenu();
                }
            }

            return selected;
        }

        public bool AssertMenuItem(string itemName, bool expectDisabled)
        {
            if (string.IsNullOrWhiteSpace(itemName))
            {
                return false;
            }

            DropdownMenuAction.Status expectedStatus = expectDisabled
                ? DropdownMenuAction.Status.Disabled
                : DropdownMenuAction.Status.Normal;

            Codingriver.Logger.Log($"[UPilot Flow] menu assertion item={itemName} expected={expectedStatus} contextDisplayed={_contextMenuSimulator?.menuIsDisplayed == true} contextItems={_contextMenuSimulator?.menuItemCount ?? 0} popupDisplayed={_popupMenuSimulator?.menuIsDisplayed == true} popupItems={_popupMenuSimulator?.menuItemCount ?? 0}");

            try
            {
                if (_contextMenuSimulator != null && _contextMenuSimulator.menuIsDisplayed)
                {
                    AssertContainsAnyMenuAction(_contextMenuSimulator, itemName, expectedStatus);
                    return true;
                }

                if (_popupMenuSimulator != null && _popupMenuSimulator.menuIsDisplayed)
                {
                    AssertContainsAnyMenuAction(_popupMenuSimulator, itemName, expectedStatus);
                    return true;
                }
            }
            catch (AssertionException)
            {
                return false;
            }

            return false;
        }

        public string DescribeState()
        {
            return $"contextDisplayed={_contextMenuSimulator?.menuIsDisplayed == true},contextItems={_contextMenuSimulator?.menuItemCount ?? 0},popupDisplayed={_popupMenuSimulator?.menuIsDisplayed == true},popupItems={_popupMenuSimulator?.menuItemCount ?? 0}";
        }

        public void Dispose()
        {
            if (_fixture == null)
            {
                return;
            }

            if (_contextMenuSimulator != null)
            {
                _fixture.RemoveTestComponent(_contextMenuSimulator);
                _contextMenuSimulator = null;
            }

            if (_popupMenuSimulator != null)
            {
                _fixture.RemoveTestComponent(_popupMenuSimulator);
                _popupMenuSimulator = null;
            }

            try { _fixture.FixtureTearDown(); } catch { }
            _fixture.FixtureOneTimeTearDown();
        }

        private void DiscardMenus()
        {
            _contextMenuSimulator?.DiscardMenu();
            _popupMenuSimulator?.DiscardMenu();
        }

        private static T CreateComponent<T>() where T : UITestComponent
        {
            return (T)Activator.CreateInstance(typeof(T), true);
        }

        private static void InvokeLifecycle(UITestComponent component, MethodInfo method)
        {
            if (component == null || method == null)
            {
                return;
            }

            method.Invoke(component, null);
        }

        private static VisualElement ResolvePopupTrigger(VisualElement element)
        {
            if (element == null)
            {
                return null;
            }

            return element.Q<VisualElement>(className: "unity-base-popup-field__input") ?? element;
        }

        private static void AssertContainsAnyMenuAction(ContextMenuSimulator simulator, string itemName, DropdownMenuAction.Status expectedStatus)
        {
            AssertionException last = null;
            foreach (string candidate in ActionHelpers.MenuItemNameCandidates(itemName))
            {
                try
                {
                    simulator.AssertContainsAction(candidate, expectedStatus);
                    return;
                }
                catch (AssertionException ex)
                {
                    last = ex;
                }
            }

            throw last ?? new AssertionException($"Menu item was not available: {itemName}");
        }

        private static void AssertContainsAnyMenuAction(PopupMenuSimulator simulator, string itemName, DropdownMenuAction.Status expectedStatus)
        {
            AssertionException last = null;
            foreach (string candidate in ActionHelpers.MenuItemNameCandidates(itemName))
            {
                try
                {
                    simulator.AssertContainsAction(candidate, expectedStatus);
                    return;
                }
                catch (AssertionException ex)
                {
                    last = ex;
                }
            }

            throw last ?? new AssertionException($"Menu item was not available: {itemName}");
        }
    }

    public interface IUiPointerDriver
    {
        string DriverName { get; }

        bool IsOfficial { get; }

        void Click(VisualElement element, int clickCount, UiMouseButton button, EventModifiers modifiers, ActionContext context);

        Task DragAsync(VisualElement root, Vector2 fromPos, Vector2 toPos, int delayMs, int frameCount, UiMouseButton button, EventModifiers modifiers, ActionContext context);

        void Scroll(VisualElement element, float dx, float dy, ActionContext context);

        Task HoverAsync(VisualElement element, Vector2 center, int delayMs, EventModifiers modifiers, ActionContext context);
    }

    public sealed class FallbackUiPointerDriver : IUiPointerDriver
    {
        public string DriverName => "UIToolkitFallbackOnly";

        public bool IsOfficial => false;

        public void Click(VisualElement element, int clickCount, UiMouseButton button, EventModifiers modifiers, ActionContext context)
        {
            ActionHelpers.DispatchClick(element, clickCount, button, modifiers, context);
        }

        public async Task DragAsync(VisualElement root, Vector2 fromPos, Vector2 toPos, int delayMs, int frameCount, UiMouseButton button, EventModifiers modifiers, ActionContext context)
        {
            VisualElement fromElement = root?.panel?.Pick(fromPos);
            VisualElement toElement = root?.panel?.Pick(toPos);

            ActionHelpers.DispatchMouseEvent(fromElement ?? root, EventType.MouseDown, fromPos, Vector2.zero, button: (int)button, modifiers: modifiers);

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            for (int i = 1; i <= frameCount; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Vector2 prev = Vector2.Lerp(fromPos, toPos, (float)(i - 1) / frameCount);
                Vector2 pos = Vector2.Lerp(fromPos, toPos, (float)i / frameCount);
                Vector2 delta = pos - prev;
                ActionHelpers.DispatchMouseEvent(root, EventType.MouseMove, pos, delta, button: (int)button, modifiers: modifiers);
                await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
            }

            ActionHelpers.DispatchMouseEvent(toElement ?? root, EventType.MouseUp, toPos, Vector2.zero, button: (int)button, modifiers: modifiers);

            // Ensure the source element receives MouseUp so drag-end callbacks fire.
            if (fromElement != null && !ReferenceEquals(fromElement, toElement))
            {
                var imguiEvent = new UnityEngine.Event
                {
                    type = EventType.MouseUp,
                    mousePosition = fromPos,
                    delta = Vector2.zero,
                    button = (int)button,
                    clickCount = 1,
                    modifiers = modifiers,
                };
                using (MouseUpEvent evt = MouseUpEvent.GetPooled(imguiEvent))
                {
                    evt.target = fromElement;
                    fromElement.SendEvent(evt);
                }
            }
        }

        public void Scroll(VisualElement element, float dx, float dy, ActionContext context)
        {
            if (element is ScrollView scrollView)
            {
                float nextX = scrollView.scrollOffset.x + dx;
                float nextY = scrollView.scrollOffset.y + dy;
                if (Math.Abs(dx) > 0.01f && scrollView.horizontalScroller != null)
                {
                    scrollView.horizontalScroller.value += dx;
                    nextX = scrollView.horizontalScroller.value;
                }

                if (Math.Abs(dy) > 0.01f && scrollView.verticalScroller != null)
                {
                    scrollView.verticalScroller.value += dy;
                    nextY = scrollView.verticalScroller.value;
                }

                scrollView.scrollOffset = new Vector2(nextX, nextY);
                ActionHelpers.DispatchWheelEvent(scrollView, scrollView.worldBound.center, new Vector2(dx, dy));
                context?.Log($"scroll: offset is now {scrollView.scrollOffset}");
                return;
            }

            ActionHelpers.DispatchWheelEvent(element, element.worldBound.center, new Vector2(dx, dy));
        }

        public async Task HoverAsync(VisualElement element, Vector2 center, int delayMs, EventModifiers modifiers, ActionContext context)
        {
            element.Focus();
            ActionHelpers.DispatchMouseEvent(element, EventType.MouseMove, center, Vector2.zero, 0, 0, modifiers);

            if (delayMs > 0)
            {
                context?.Log($"hover: wait {delayMs}ms");
                await EditorAsyncUtility.DelayAsync(delayMs, context.CancellationToken);
            }
        }
    }

    public sealed class OfficialUiPointerDriver : IUiPointerDriver
    {
        private readonly PanelSimulator _simulator;

        public OfficialUiPointerDriver(PanelSimulator simulator)
        {
            _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        }

        public string DriverName => "PanelSimulator";

        public bool IsOfficial => true;

        public void Click(VisualElement element, int clickCount, UiMouseButton button, EventModifiers modifiers, ActionContext context)
        {
            bool pointerUpReceived = false;
            bool mouseUpReceived = false;
            void OnPointerUp(PointerUpEvent _) => pointerUpReceived = true;
            void OnMouseUp(MouseUpEvent _) => mouseUpReceived = true;

            element.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            element.RegisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
            try
            {
                if (clickCount >= 2)
                {
                    _simulator.DoubleClick(element, button, modifiers);
                }
                else
                {
                    _simulator.Click(element, button, modifiers);
                }

                _simulator.FrameUpdate();
            }
            finally
            {
                element.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                element.UnregisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
            }

            // Some com.unity.ui versions fail to deliver the terminal input event to an
            // EditorWindow panel. Fall back only when the official simulator delivered
            // neither PointerUp nor MouseUp; unconditional fallback performs every click
            // twice on versions where PanelSimulator works correctly.
            if (element is Button && !pointerUpReceived && !mouseUpReceived)
            {
                context?.Log("click: official simulator delivered no terminal event; using validated fallback dispatch");
                ActionHelpers.DispatchClick(element, clickCount, button, modifiers, context);
            }
        }

        public async Task DragAsync(VisualElement root, Vector2 fromPos, Vector2 toPos, int delayMs, int frameCount, UiMouseButton button, EventModifiers modifiers, ActionContext context)
        {
            _simulator.DragAndDrop(fromPos, toPos, button, modifiers);
            _simulator.FrameUpdate();

            // PanelSimulator.DragAndDrop does not reliably dispatch MouseUp to the source element
            // when the drag ends on a different target. Ensure drag-end callbacks fire.
            VisualElement fromElement = root?.panel?.Pick(fromPos);
            VisualElement toElement = root?.panel?.Pick(toPos);
            if (fromElement != null && !ReferenceEquals(fromElement, toElement))
            {
                var imguiEvent = new UnityEngine.Event
                {
                    type = EventType.MouseUp,
                    mousePosition = fromPos,
                    delta = Vector2.zero,
                    button = (int)button,
                    clickCount = 1,
                    modifiers = modifiers,
                };
                using (MouseUpEvent evt = MouseUpEvent.GetPooled(imguiEvent))
                {
                    evt.target = fromElement;
                    fromElement.SendEvent(evt);
                }
            }

            await Task.CompletedTask;
        }

        public void Scroll(VisualElement element, float dx, float dy, ActionContext context)
        {
            _simulator.ScrollWheel(element, new Vector2(dx, dy));
            _simulator.FrameUpdate();
        }

        public async Task HoverAsync(VisualElement element, Vector2 center, int delayMs, EventModifiers modifiers, ActionContext context)
        {
            _simulator.MouseMove(element, modifiers);
            _simulator.FrameUpdate();

            if (delayMs > 0)
            {
                context?.Log($"hover: wait {delayMs}ms");
                await EditorAsyncUtility.DelayAsync(delayMs, context.CancellationToken);
            }
        }
    }

    public sealed class UPilotFlowSimulationSession : IDisposable
    {
        private static readonly MethodInfo EventHelpersSetUpMethod = Type.GetType("UnityEngine.UIElements.TestFramework.EventHelpers, Unity.UI.TestFramework.Runtime")
            ?.GetMethod("TestSetUp", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly MethodInfo EventHelpersTearDownMethod = Type.GetType("UnityEngine.UIElements.TestFramework.EventHelpers, Unity.UI.TestFramework.Runtime")
            ?.GetMethod("TestTearDown", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo EventHelpersInitializedTestIdField = Type.GetType("UnityEngine.UIElements.TestFramework.EventHelpers, Unity.UI.TestFramework.Runtime")
            ?.GetField("_initializedTestId", BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly object EventLifecycleGate = new object();
        private static string s_FlowOwnedEventTestId;
        private static int s_FlowOwnedEventLeaseCount;

        private readonly InputTestFixture _inputTestFixture = new InputTestFixture();
        private IUiPointerDriver _pointerDriver;
        private OfficialEditorWindowHostBridge _officialHostBridge;
        private OfficialUiMenuDriver _menuDriver;
        private VisualElement _fallbackMenuElement;
        private KeyboardState _keyboardState;
        private string _keyboardDriverName = "UIToolkitFallbackOnly";
        private bool _inputSystemReady;
        private bool _officialEventLifecycleReady;
        private bool _joinedFlowOwnedEventLifecycle;
        private string _officialEventLifecycleTestId;

        public UPilotFlowSimulationSession()
        {
            OfficialUiToolkit = OfficialUiToolkitTestAvailability.Detect();
            _pointerDriver = new FallbackUiPointerDriver();
            HostDriverName = "RootOverrideOnly";
        }

        public OfficialUiToolkitTestAvailability OfficialUiToolkit { get; }

        public IUiPointerDriver PointerDriver => _pointerDriver;

        public PanelSimulator PanelSimulator => _officialHostBridge?.Simulator;

        public Keyboard Keyboard { get; private set; }

        public Mouse Mouse { get; private set; }

        public string HostDriverName { get; private set; }

        public bool IsInputSystemReady => _inputSystemReady && Keyboard != null;

        public bool HasExecutableOfficialHost => _officialHostBridge != null;

        public EditorWindow HostEditorWindow => _officialHostBridge?.Window;

        public bool HasExecutableOfficialPointerDriver => PointerDriver != null && PointerDriver.IsOfficial;

        public bool HasExecutableOfficialKeyboardDriver => PanelSimulator != null;

        public string KeyboardDriverName => _keyboardDriverName;

        public string PointerDriverName => PointerDriver?.DriverName ?? "UIToolkitFallbackOnly";

        public bool BindEditorWindowHost(EditorWindow window, string fallbackHostDriverName)
        {
            ReleaseOfficialHostBridge();
            BindHostDriver(fallbackHostDriverName);

            if (window == null || !OfficialUiToolkit.HasEditorWindowPanelSimulator || !OfficialUiToolkit.HasPanelSimulator)
            {
                return false;
            }

            try
            {
                _officialHostBridge = new OfficialEditorWindowHostBridge(window);
                BindPanelSimulator(_officialHostBridge.Simulator);
                HostDriverName = _officialHostBridge.HostDriverName;
                MarkKeyboardOfficial();
                return true;
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] Failed to bind official EditorWindow host bridge: {ex}");
                ReleaseOfficialHostBridge();
                _pointerDriver = new FallbackUiPointerDriver();
                MarkKeyboardFallback();
                BindHostDriver(fallbackHostDriverName);
                return false;
            }
        }

        public void BindPanelSimulator(PanelSimulator simulator)
        {
            ReleaseMenuDriver();
            if (simulator != null)
            {
                EnsureOfficialEventLifecycle();
                _pointerDriver = new OfficialUiPointerDriver(simulator);
                MarkKeyboardOfficial();
            }
        }

        public void BindHostDriver(string hostDriverName)
        {
            HostDriverName = string.IsNullOrWhiteSpace(hostDriverName)
                ? "RootOverrideOnly"
                : hostDriverName;
        }

        public void MarkKeyboardOfficial()
        {
            _keyboardDriverName = "PanelSimulator";
        }

        public void MarkKeyboardInputSystem()
        {
            _keyboardDriverName = "InputSystemTestFramework+UIToolkitBridge";
        }

        public void MarkKeyboardFallback()
        {
            _keyboardDriverName = "UIToolkitFallbackOnly";
        }

        public bool TryPressKeyWithOfficialDriver(VisualElement target, KeyCode keyCode, ActionContext context)
        {
            if (PanelSimulator == null || context?.Options?.RequireInputSystemKeyboardDriver == true)
            {
                return false;
            }

            try
            {
                target?.Focus();
                PanelSimulator.FrameUpdate();

                switch (keyCode)
                {
                    case KeyCode.Tab:
                        PanelSimulator.TabKeyPress();
                        break;
                    case KeyCode.Return:
                        PanelSimulator.ReturnKeyPress();
                        break;
                    case KeyCode.KeypadEnter:
                        PanelSimulator.KeypadEnterKeyPress();
                        break;
                    default:
                        PanelSimulator.KeyPress(keyCode);
                        break;
                }

                PanelSimulator.FrameUpdate();
                MarkKeyboardOfficial();
                return true;
            }
            catch (Exception ex)
            {
                context?.Log($"press_key: official PanelSimulator path failed, fallback to compatibility path. reason={ex.Message}");
                return false;
            }
        }

        public bool TryTypeTextWithOfficialDriver(VisualElement element, string value, ActionContext context)
        {
            if (PanelSimulator == null || context?.Options?.RequireInputSystemKeyboardDriver == true)
            {
                return false;
            }

            try
            {
                element?.Focus();
                PanelSimulator.FrameUpdate();
                PanelSimulator.TypingText(value ?? string.Empty);
                PanelSimulator.FrameUpdate();
                MarkKeyboardOfficial();
                return true;
            }
            catch (Exception ex)
            {
                context?.Log($"type_text: official PanelSimulator path failed, fallback to compatibility path. reason={ex.Message}");
                return false;
            }
        }

        public bool TryExecuteCommandWithOfficialDriver(VisualElement target, string commandName, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                target?.Focus();
                PanelSimulator.FrameUpdate();
                PanelSimulator.ExecuteCommand(commandName);
                PanelSimulator.FrameUpdate();
                return true;
            }
            catch (Exception ex)
            {
                context?.Log($"execute_command: official PanelSimulator path failed, fallback to compatibility path. reason={ex.Message}");
                return false;
            }
        }

        public bool TryValidateCommandWithOfficialDriver(VisualElement target, string commandName, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                target?.Focus();
                PanelSimulator.FrameUpdate();
                PanelSimulator.ValidateCommand(commandName);
                PanelSimulator.FrameUpdate();
                return true;
            }
            catch (Exception ex)
            {
                context?.Log($"validate_command: official PanelSimulator path failed, fallback to compatibility path. reason={ex.Message}");
                return false;
            }
        }

        public bool TryOpenContextMenu(VisualElement target, EventModifiers modifiers, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                return EnsureMenuDriver().OpenContextMenu(target, modifiers);
            }
            catch (Exception ex)
            {
                context?.Log($"open_context_menu: official MenuSimulator path failed. reason={ex.Message}");
                ReleaseMenuDriver();
                return false;
            }
        }

        public bool TrySelectContextMenuItem(string itemName, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                return EnsureMenuDriver().SelectContextMenuItem(itemName);
            }
            catch (Exception ex)
            {
                context?.Log($"select_context_menu_item: official MenuSimulator path failed. reason={ex.Message}");
                ReleaseMenuDriver();
                return false;
            }
        }

        public bool TryOpenPopupMenu(VisualElement target, EventModifiers modifiers, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                return EnsureMenuDriver().OpenPopupMenu(target, modifiers);
            }
            catch (Exception ex)
            {
                context?.Log($"open_popup_menu: official MenuSimulator path failed. reason={ex.Message}");
                ReleaseMenuDriver();
                return false;
            }
        }

        public bool TrySelectPopupMenuItem(string itemName, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                return EnsureMenuDriver().SelectPopupMenuItem(itemName);
            }
            catch (Exception ex)
            {
                context?.Log($"select_popup_menu_item: official MenuSimulator path failed. reason={ex.Message}");
                ReleaseMenuDriver();
                return false;
            }
        }

        public bool TryAssertMenuItem(string itemName, bool expectDisabled, ActionContext context)
        {
            if (PanelSimulator == null)
            {
                return false;
            }

            try
            {
                return EnsureMenuDriver().AssertMenuItem(itemName, expectDisabled);
            }
            catch (Exception ex)
            {
                context?.Log($"assert_menu_item: official MenuSimulator path failed. reason={ex.Message}");
                ReleaseMenuDriver();
                return false;
            }
        }

        public string DescribeDrivers()
        {
            return $"host={HostDriverName}; pointer={PointerDriverName}; keyboard={KeyboardDriverName}; official={OfficialUiToolkit.Describe()}";
        }

        public void RememberFallbackMenuElement(VisualElement element)
        {
            _fallbackMenuElement = element;
        }

        public bool TrySelectRememberedDropdownMenuItem(string itemName)
        {
            VisualElement element = _fallbackMenuElement;
            if (element == null)
                return false;
            bool selected = ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
            if (selected)
                _fallbackMenuElement = null;
            return selected;
        }

        public bool TryFindRememberedDropdownMenuItem(string itemName, out DropdownMenuAction action)
        {
            return ActionHelpers.TryFindDropdownMenuAction(_fallbackMenuElement, itemName, out action);
        }

        public string DescribeMenuState()
        {
            return _menuDriver?.DescribeState() ?? "menuDriver=null";
        }

        public void EnsureInputSystemReady()
        {
            if (_inputSystemReady)
            {
                return;
            }

            try
            {
                _inputTestFixture.Setup();
                Keyboard = InputSystem.AddDevice<Keyboard>();
                Mouse = InputSystem.AddDevice<Mouse>();
                Mouse?.MakeCurrent();
                Keyboard?.MakeCurrent();
                _keyboardState = new KeyboardState();
                _inputSystemReady = Keyboard != null;
                MarkKeyboardInputSystem();
            }
            catch (Exception ex)
            {
                throw new UPilotFlowException(
                    ErrorCodes.InputSystemTestFrameworkUnavailable,
                    $"Failed to initialize InputSystem test session: {ex.Message}",
                    ex);
            }
        }

        public void PressKey(Key key)
        {
            EnsureInputSystemReady();

            _keyboardState.Press(key);
            InputSystem.QueueStateEvent(Keyboard, _keyboardState);
            InputSystem.Update();

            _keyboardState.Release(key);
            InputSystem.QueueStateEvent(Keyboard, _keyboardState);
            InputSystem.Update();
        }

        public void SendText(string value)
        {
            EnsureInputSystemReady();

            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (char character in value)
            {
                InputSystem.QueueTextEvent(Keyboard, character);
                InputSystem.Update();
            }
        }

        private void ReleaseOfficialHostBridge()
        {
            ReleaseMenuDriver();
            if (_officialHostBridge == null)
            {
                return;
            }

            try
            {
                _officialHostBridge.Dispose();
            }
            finally
            {
                _officialHostBridge = null;
            }
        }

        private OfficialUiMenuDriver EnsureMenuDriver()
        {
            if (_menuDriver == null)
            {
                // Transfer EventHelpers ownership from the lightweight session lease to
                // the official UITestFixture. CommonUITestFixture must be in DuringTest
                // state for FrameUpdate and menu components, and it owns TestSetUp/
                // TestTearDown while the menu driver is alive.
                ReleaseOfficialEventLifecycle();
                try
                {
                    _menuDriver = new OfficialUiMenuDriver(PanelSimulator);
                }
                catch
                {
                    EnsureOfficialEventLifecycle();
                    throw;
                }
            }

            return _menuDriver;
        }

        private void ReleaseMenuDriver()
        {
            if (_menuDriver == null)
            {
                return;
            }

            try
            {
                _menuDriver.Dispose();
            }
            finally
            {
                _menuDriver = null;
                EnsureOfficialEventLifecycle();
            }
        }

        public void Dispose()
        {
            _fallbackMenuElement = null;
            ReleaseOfficialHostBridge();
            ReleaseOfficialEventLifecycle();

            if (!_inputSystemReady)
            {
                return;
            }

            try
            {
                _inputTestFixture.TearDown();
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] Failed to tear down InputSystem test session: {ex.Message}");
            }
            finally
            {
                Keyboard = null;
                Mouse = null;
                _keyboardState = default;
                _inputSystemReady = false;
            }
        }

        private static TestExecutionContext s_FakeTestContext;

        private static void EnsureFakeNUnitTestContext()
        {
            if (TestContext.CurrentTestExecutionContext != null)
            {
                Codingriver.Logger.Log($"[UPilot Flow] EnsureFakeNUnitTestContext: already set, id={TestContext.CurrentTestExecutionContext.CurrentTest?.Id}");
                return;
            }
            if (s_FakeTestContext == null)
            {
                s_FakeTestContext = new TestExecutionContext();
                s_FakeTestContext.CurrentTest = new TestSuite("CodingRiver.UPilot.Flow.FakeTest");
            }
            TestContext.CurrentTestExecutionContext = s_FakeTestContext;
            Codingriver.Logger.Log($"[UPilot Flow] EnsureFakeNUnitTestContext: injected fake context, id={s_FakeTestContext.CurrentTest?.Id}");
        }

        private void EnsureOfficialEventLifecycle()
        {
            if (_officialEventLifecycleReady)
            {
                return;
            }

            if (EventHelpersSetUpMethod != null)
            {
                try
                {
                    EnsureFakeNUnitTestContext();
                    string currentTestId = TestContext.CurrentTestExecutionContext.CurrentTest.Id;

                    lock (EventLifecycleGate)
                    {
                        string initializedTestId = EventHelpersInitializedTestIdField?.GetValue(null) as string;
                        if (string.Equals(initializedTestId, currentTestId, StringComparison.Ordinal))
                        {
                            if (s_FlowOwnedEventLeaseCount > 0 &&
                                string.Equals(s_FlowOwnedEventTestId, currentTestId, StringComparison.Ordinal))
                            {
                                s_FlowOwnedEventLeaseCount++;
                                _joinedFlowOwnedEventLifecycle = true;
                            }

                            _officialEventLifecycleTestId = currentTestId;
                            _officialEventLifecycleReady = true;
                            return;
                        }

                        if (!string.IsNullOrEmpty(initializedTestId))
                        {
                            EventHelpersTearDownMethod?.Invoke(null, null);
                        }

                        EventHelpersSetUpMethod.Invoke(null, null);
                        s_FlowOwnedEventTestId = currentTestId;
                        s_FlowOwnedEventLeaseCount = 1;
                        _joinedFlowOwnedEventLifecycle = true;
                        _officialEventLifecycleTestId = currentTestId;
                        _officialEventLifecycleReady = true;
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.ToString() ?? "(no inner exception)";
                    Codingriver.Logger.LogWarning($"[UPilot Flow] EventHelpers.TestSetUp failed: {ex.Message}. Inner: {inner}");
                    try
                    {
                        var pointerStateType = Type.GetType("UnityEngine.UIElements.PointerDeviceState, UnityEngine.UIElementsModule");
                        pointerStateType?.GetMethod("Reset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
                    }
                    catch { }
                }
            }
            else
            {
                _officialEventLifecycleReady = true;
            }
        }

        private void ReleaseOfficialEventLifecycle()
        {
            if (!_officialEventLifecycleReady)
            {
                return;
            }

            try
            {
                if (_joinedFlowOwnedEventLifecycle)
                {
                    lock (EventLifecycleGate)
                    {
                        if (s_FlowOwnedEventLeaseCount > 0 &&
                            string.Equals(s_FlowOwnedEventTestId, _officialEventLifecycleTestId, StringComparison.Ordinal))
                        {
                            s_FlowOwnedEventLeaseCount--;
                            if (s_FlowOwnedEventLeaseCount == 0)
                            {
                                string initializedTestId = EventHelpersInitializedTestIdField?.GetValue(null) as string;
                                if (string.Equals(initializedTestId, _officialEventLifecycleTestId, StringComparison.Ordinal))
                                {
                                    EventHelpersTearDownMethod?.Invoke(null, null);
                                }

                                s_FlowOwnedEventTestId = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] Failed to tear down UI Test Framework event lifecycle: {ex.Message}");
            }
            finally
            {
                _officialEventLifecycleReady = false;
                _joinedFlowOwnedEventLifecycle = false;
                _officialEventLifecycleTestId = null;
            }
        }
    }
}
