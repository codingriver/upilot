using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public static class SampleWindowAssetLoader
    {
        public static void RebuildWindow(EditorWindow window, string uxmlPath, string ussPath)
        {
            if (window == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, "Sample window is null.");
            }

            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (tree == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, $"Missing UXML asset: {uxmlPath}");
            }

            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            if (styleSheet == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, $"Missing USS asset: {ussPath}");
            }

            VisualElement root = window.rootVisualElement;
            root.Clear();
            if (!root.styleSheets.Contains(styleSheet))
            {
                root.styleSheets.Add(styleSheet);
            }

            tree.CloneTree(root);
        }
    }

    /// <summary>
    /// Sample window used by UnityUIFlow examples and tests.
    /// </summary>
    public sealed class SampleLoginWindow : EditorWindow
    {
        public const string UxmlPath = "Assets/Examples/Uxml/SampleLoginWindow.uxml";
        public const string UssPath = "Assets/Examples/Uss/SampleLoginWindow.uss";

        private TextField _usernameField;
        private TextField _passwordField;
        private Label _statusLabel;
        private Label _toast;
        private Button _loginButton;
        private Button _resetButton;
        private Button _saveButton;
        private double _toastHideAtTime;

        [MenuItem("UnityUIFlow/Samples/Login Window")]
        public static void Open()
        {
            SampleLoginWindow window = GetWindow<SampleLoginWindow>();
            window.titleContent = new GUIContent("Sample Login");
            window.minSize = new Vector2(480f, 360f);
            window.Show();
        }

        private void OnEnable()
        {
            BuildUi();
        }

        private void Update()
        {
            if (_toast == null || _toastHideAtTime <= 0d)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup >= _toastHideAtTime)
            {
                _toast.style.display = DisplayStyle.None;
                _toastHideAtTime = 0d;
            }
        }

        public void BuildUi()
        {
            SampleWindowAssetLoader.RebuildWindow(this, UxmlPath, UssPath);

            _usernameField = rootVisualElement.Q<TextField>("username-input");
            _passwordField = rootVisualElement.Q<TextField>("password-input");
            _statusLabel = rootVisualElement.Q<Label>("status-label");
            _toast = rootVisualElement.Q<Label>("toast-message");
            _loginButton = rootVisualElement.Q<Button>("login-button");
            _resetButton = rootVisualElement.Q<Button>("reset-button");
            _saveButton = rootVisualElement.Q<Button>("save-button");

            if (_usernameField == null || _passwordField == null || _statusLabel == null || _toast == null
                || _loginButton == null || _resetButton == null || _saveButton == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, "Sample login UXML is missing required elements.");
            }

            _loginButton.userData = new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["data-role"] = "primary",
            };

            _saveButton.userData = new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["data-role"] = "secondary",
            };

            _loginButton.RegisterCallback<MouseUpEvent>(_ => HandleLogin());
            _resetButton.RegisterCallback<MouseUpEvent>(_ => HandleReset());
            _saveButton.RegisterCallback<MouseUpEvent>(_ => ShowToastForFrames(60));

            _statusLabel.text = "Idle";
            _toast.style.display = DisplayStyle.None;
            _toastHideAtTime = 0d;
        }

        public void FillCredentials(string username, string password)
        {
            _usernameField.value = username;
            _passwordField.value = password;
        }

        public void ShowToastForFrames(int frameCount)
        {
            _toast.text = "Saved";
            _toast.style.display = DisplayStyle.Flex;

            // Editor update frequency is not stable during automated runs, so map the sample's
            // "frame" lifetime to a small real-time window instead of raw update counts.
            double lifetimeSeconds = System.Math.Max(frameCount, 1) * 0.05d;
            _toastHideAtTime = EditorApplication.timeSinceStartup + lifetimeSeconds;
        }

        private void HandleLogin()
        {
            bool success = !string.IsNullOrWhiteSpace(_usernameField.value) && !string.IsNullOrWhiteSpace(_passwordField.value);
            _statusLabel.text = success ? $"Welcome {_usernameField.value}" : "Missing credentials";
        }

        private void HandleReset()
        {
            _usernameField.value = string.Empty;
            _passwordField.value = string.Empty;
            _statusLabel.text = "Idle";
        }
    }

    /// <summary>
    /// Interaction-focused sample window backed by UXML/USS assets.
    /// </summary>
    public sealed class SampleInteractionWindow : EditorWindow
    {
        public const string UxmlPath = "Assets/Examples/Uxml/SampleInteractionWindow.uxml";
        public const string UssPath = "Assets/Examples/Uss/SampleInteractionWindow.uss";

        private TextField _inputField;
        private Label _clickStatus;
        private Label _doubleClickStatus;
        private Label _hoverStatus;
        private Label _keyStatus;
        private Label _dragStatus;
        private Label _pointerStatus;
        private Label _menuStatus;
        private ScrollView _scrollView;
        private DropdownField _popupMenuDropdown;
        private int _clickCount;
        private int _doubleClickCount;
        private bool _dragStarted;

        [MenuItem("UnityUIFlow/Samples/Interaction Window")]
        public static void Open()
        {
            SampleInteractionWindow window = GetWindow<SampleInteractionWindow>();
            window.titleContent = new GUIContent("Sample Interaction");
            window.minSize = new Vector2(540f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            BuildUi();
        }

        public void BuildUi()
        {
            SampleWindowAssetLoader.RebuildWindow(this, UxmlPath, UssPath);

            _inputField = rootVisualElement.Q<TextField>("interaction-input");
            _clickStatus = rootVisualElement.Q<Label>("click-status");
            _doubleClickStatus = rootVisualElement.Q<Label>("double-click-status");
            _hoverStatus = rootVisualElement.Q<Label>("hover-status");
            _keyStatus = rootVisualElement.Q<Label>("key-status");
            _dragStatus = rootVisualElement.Q<Label>("drag-status");
            _pointerStatus = rootVisualElement.Q<Label>("pointer-status");
            _menuStatus = rootVisualElement.Q<Label>("menu-status");
            _scrollView = rootVisualElement.Q<ScrollView>("scroll-view");
            _popupMenuDropdown = rootVisualElement.Q<DropdownField>("popup-menu-dropdown");
            Button clickButton = rootVisualElement.Q<Button>("click-button");
            Button doubleClickButton = rootVisualElement.Q<Button>("double-click-button");
            VisualElement hoverTarget = rootVisualElement.Q<VisualElement>("hover-target");
            Label contextMenuTarget = rootVisualElement.Q<Label>("context-menu-target");

            if (_inputField == null || _clickStatus == null || _doubleClickStatus == null || _hoverStatus == null
                || _keyStatus == null || _dragStatus == null || _pointerStatus == null || _menuStatus == null
                || _popupMenuDropdown == null || _scrollView == null
                || clickButton == null || doubleClickButton == null || hoverTarget == null || contextMenuTarget == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, "Sample interaction UXML is missing required elements.");
            }

            clickButton.userData = new Dictionary<string, object>(System.StringComparer.Ordinal)
            {
                ["data-action"] = "single-click",
            };

            hoverTarget.userData = new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["data-zone"] = "hover",
            };

            _clickCount = 0;
            _doubleClickCount = 0;
            _dragStarted = false;
            _clickStatus.text = "Clicks: 0";
            _doubleClickStatus.text = "Double Clicks: 0";
            _hoverStatus.text = "Hover: idle";
            _keyStatus.text = "Key: none";
            _dragStatus.text = "Drag: idle";
            _pointerStatus.text = "Pointer: none";
            _menuStatus.text = "Menu: none";
            _scrollView.scrollOffset = Vector2.zero;
            _popupMenuDropdown.choices = new List<string> { "Option1", "Option2" };
            _popupMenuDropdown.index = -1;

            clickButton.RegisterCallback<MouseUpEvent>(evt =>
            {
                _clickCount++;
                _clickStatus.text = $"Clicks: {_clickCount}";
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });

            doubleClickButton.RegisterCallback<ClickEvent>(evt =>
            {
                _doubleClickCount++;
                _doubleClickStatus.text = $"Double Clicks: {_doubleClickCount}";
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });

            hoverTarget.RegisterCallback<MouseOverEvent>(_ =>
            {
                _hoverStatus.text = "Hover: active";
            });

            contextMenuTarget.RegisterCallback<ContextualMenuPopulateEvent>(evt =>
            {
                evt.menu.AppendAction("Copy", _ => _menuStatus.text = "Context: Copy");
                evt.menu.AppendAction("Delete", _ => _menuStatus.text = "Context: Delete", _ => DropdownMenuAction.Status.Disabled);
            });

            _popupMenuDropdown.RegisterValueChangedCallback(evt =>
            {
                if (!string.IsNullOrWhiteSpace(evt.newValue))
                {
                    _menuStatus.text = $"Popup: {evt.newValue}";
                }
            });

            rootVisualElement.RegisterCallback<KeyDownEvent>(evt =>
            {
                _keyStatus.text = $"Key: {evt.keyCode}";
            });

            rootVisualElement.RegisterCallback<ValidateCommandEvent>(evt =>
            {
                _keyStatus.text = $"Validate: {evt.commandName}";
            });

            rootVisualElement.RegisterCallback<ExecuteCommandEvent>(evt =>
            {
                _keyStatus.text = $"Execute: {evt.commandName}";
            });

            rootVisualElement.RegisterCallback<MouseDownEvent>(evt =>
            {
                _dragStarted = true;
                _dragStatus.text = "Drag: started";
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });
            rootVisualElement.RegisterCallback<PointerDownEvent>(evt =>
            {
                _dragStarted = true;
                _dragStatus.text = "Drag: started";
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });
            rootVisualElement.RegisterCallback<MouseMoveEvent>(evt =>
            {
                if (_dragStarted && evt.mouseDelta.sqrMagnitude > 0f)
                {
                    _dragStatus.text = "Drag: moving";
                }
            });
            rootVisualElement.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_dragStarted && evt.deltaPosition.sqrMagnitude > 0f)
                {
                    _dragStatus.text = "Drag: moving";
                }
            });
            rootVisualElement.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (_dragStarted)
                {
                    _dragStatus.text = "Drag: completed";
                    _dragStarted = false;
                }
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });
            rootVisualElement.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_dragStarted)
                {
                    _dragStatus.text = "Drag: completed";
                    _dragStarted = false;
                }
                _pointerStatus.text = $"Pointer: button={evt.button}, modifiers={evt.modifiers}";
            });
        }

        public void FocusInput()
        {
            _inputField?.Focus();
        }
    }

    /// <summary>
    /// Sample Page Object for the login window.
    /// </summary>
    public sealed class LoginPage
    {
        private readonly VisualElement _root;

        public LoginPage(VisualElement root)
        {
            _root = root;
        }

        public Task LoginAsync(string username, string password)
        {
            if (_root.Q<TextField>("username-input") is TextField usernameField)
            {
                usernameField.value = username;
            }

            if (_root.Q<TextField>("password-input") is TextField passwordField)
            {
                passwordField.value = password;
            }

            if (_root.Q<Button>("login-button") is Button loginButton)
            {
                ActionHelpers.DispatchClick(loginButton, 1, MouseButton.LeftMouse, EventModifiers.None);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Sample custom action that performs a login flow.
    /// </summary>
    [ActionName("custom_login")]
    public sealed class CustomLoginAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string usernameSelector = ActionHelpers.Require(parameters, "custom_login", "username_selector");
            string passwordSelector = ActionHelpers.Require(parameters, "custom_login", "password_selector");
            string buttonSelector = ActionHelpers.Require(parameters, "custom_login", "button_selector");
            string username = ActionHelpers.Require(parameters, "custom_login", "username");
            string password = ActionHelpers.Require(parameters, "custom_login", "password");

            FindResult usernameResult = await context.Finder.WaitForElementAsync(new SelectorCompiler().Compile(usernameSelector), root, new WaitOptions
            {
                TimeoutMs = context.Options.DefaultTimeoutMs,
                PollIntervalMs = 16,
                RequireVisible = true,
            }, context.CancellationToken);

            FindResult passwordResult = await context.Finder.WaitForElementAsync(new SelectorCompiler().Compile(passwordSelector), root, new WaitOptions
            {
                TimeoutMs = context.Options.DefaultTimeoutMs,
                PollIntervalMs = 16,
                RequireVisible = true,
            }, context.CancellationToken);

            FindResult buttonResult = await context.Finder.WaitForElementAsync(new SelectorCompiler().Compile(buttonSelector), root, new WaitOptions
            {
                TimeoutMs = context.Options.DefaultTimeoutMs,
                PollIntervalMs = 16,
                RequireVisible = true,
            }, context.CancellationToken);

            VisualElement usernameElement = usernameResult.Element;
            VisualElement passwordElement = passwordResult.Element;
            VisualElement buttonElement = buttonResult.Element;

            if (!ActionHelpers.TryAssignFieldValue(usernameElement, username) || !ActionHelpers.TryAssignFieldValue(passwordElement, password))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionTargetTypeInvalid, "custom_login target type is not assignable.");
            }

            ActionHelpers.DispatchClick(buttonElement, 1, MouseButton.LeftMouse, EventModifiers.None);
        }
    }
}
