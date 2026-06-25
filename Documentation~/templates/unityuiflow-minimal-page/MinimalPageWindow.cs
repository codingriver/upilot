using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YourCompany.YourFeature
{
    public sealed class MinimalPageWindow : EditorWindow, IUnityUIFlowTestHostWindow
    {
        private const string UxmlPath = "Assets/YourFeature/Uxml/MinimalPageWindow.uxml";
        private const string UssPath = "Assets/YourFeature/Uss/MinimalPageWindow.uss";

        private TextField _usernameField;
        private TextField _passwordField;
        private Label _statusLabel;
        private VisualElement _toastHost;
        private Button _submitButton;
        private double _toastHideAtTime;

        [MenuItem("Tools/YourFeature/Minimal Page")]
        public static void Open()
        {
            MinimalPageWindow window = GetWindow<MinimalPageWindow>();
            window.titleContent = new GUIContent("Minimal Page");
            window.minSize = new Vector2(420f, 260f);
            window.Show();
        }

        public void PrepareForAutomatedTest()
        {
            titleContent = new GUIContent("Minimal Page");
            minSize = new Vector2(420f, 260f);
            BuildUi();
        }

        private void OnEnable()
        {
            PrepareForAutomatedTest();
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
        }

        private void BuildUi()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (tree == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, $"Missing UXML asset: {UxmlPath}");
            }

            if (styleSheet == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, $"Missing USS asset: {UssPath}");
            }

            rootVisualElement.Clear();
            if (!rootVisualElement.styleSheets.Contains(styleSheet))
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            tree.CloneTree(rootVisualElement);

            _usernameField = rootVisualElement.Q<TextField>("username-input");
            _passwordField = rootVisualElement.Q<TextField>("password-input");
            _submitButton = rootVisualElement.Q<Button>("submit-button");
            _statusLabel = rootVisualElement.Q<Label>("status-label");
            _toastHost = rootVisualElement.Q<VisualElement>("toast-host");

            if (_usernameField == null || _passwordField == null || _submitButton == null
                || _statusLabel == null || _toastHost == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, "MinimalPageWindow is missing required named elements.");
            }

            _submitButton.RegisterCallback<MouseUpEvent>(_ => HandleSubmit());

            _statusLabel.text = "Idle";
            ClearToast();
        }

        private void HandleSubmit()
        {
            bool success = !string.IsNullOrWhiteSpace(_usernameField.value)
                && !string.IsNullOrWhiteSpace(_passwordField.value);

            _statusLabel.text = success
                ? $"Welcome {_usernameField.value}"
                : "Missing credentials";

            ShowToast(success ? "Saved" : "Validation failed", 0.5d);
        }

        private void ShowToast(string text, double lifetimeSeconds)
        {
            ClearToast();

            var toast = new Label(text)
            {
                name = "toast-message",
            };
            toast.AddToClassList("page-toast");
            _toastHost.Add(toast);
            _toastHideAtTime = EditorApplication.timeSinceStartup + Math.Max(lifetimeSeconds, 0.1d);
        }

        private void ClearToast()
        {
            if (_toastHost == null)
            {
                return;
            }

            VisualElement toast = _toastHost.Q<VisualElement>("toast-message");
            if (toast != null)
            {
                toast.RemoveFromHierarchy();
            }

            _toastHideAtTime = 0d;
        }

        private void Tick()
        {
            if (_toastHideAtTime <= 0d || _toastHost == null)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup >= _toastHideAtTime)
            {
                ClearToast();
            }
        }
    }
}
