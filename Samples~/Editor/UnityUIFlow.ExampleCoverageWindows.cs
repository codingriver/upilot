using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow.Examples
{
    public abstract class ExampleCoverageWindowBase : ExampleAcceptanceWindowBase
    {
        protected override string UxmlPath => "Assets/Examples/Uxml/ExampleAdvancedControlsWindow.uxml";

        protected VisualElement CoverageHost => rootVisualElement.Q<VisualElement>("advanced-controls-host");

        protected void ResetHost(string title)
        {
            CoverageHost.Clear();
            Label label = rootVisualElement.Q<Label>("advanced-title");
            if (label != null)
            {
                label.text = title;
            }
        }
    }

    public sealed class ExampleCoverageFieldsWindow : ExampleCoverageWindowBase
    {
        [Serializable]
        private sealed class CoverageData : ScriptableObject
        {
            public string title = "Draft";
            public int count = 2;
            public bool enabled = true;
        }

        private enum DemoMode { Basic, Advanced, Expert }
        [Flags] private enum DemoPermissions { None = 0, Read = 1, Write = 2, Execute = 4 }
        private CoverageData _data;

        protected override string WindowTitle => "Example Coverage Fields";

        public override void PrepareForAutomatedTest()
        {
            base.PrepareForAutomatedTest();
            minSize = new Vector2(420f, 800f);
            position = new Rect(position.x, position.y, 600f, 800f);
        }

        protected override void OnEnable()
        {
            if (_data == null)
            {
                _data = CreateInstance<CoverageData>();
                _data.hideFlags = HideFlags.HideAndDontSave;
            }

            base.OnEnable();
        }

        private void OnDisable()
        {
            if (_data != null)
            {
                DestroyImmediate(_data);
                _data = null;
            }
        }

        protected override void AfterBuild()
        {
            ResetHost("Coverage Fields");

            var so = new SerializedObject(_data);
            var propertyField = new PropertyField(so.FindProperty("title"), "Property Title") { name = "property-title-field" };
            propertyField.Bind(so);
            VisualElement inspector = new InspectorElement(so) { name = "settings-inspector" };

            CoverageHost.Add(new DropdownField("Choice", new List<string> { "Alpha", "Beta", "Gamma" }, 0) { name = "choice-dropdown" });
            CoverageHost.Add(new Toggle("Enabled") { name = "enabled-toggle", value = false });
            CoverageHost.Add(new IntegerField("Count") { name = "integer-field", value = 0 });
            CoverageHost.Add(new LongField("Long Count") { name = "long-field", value = 0L });
            CoverageHost.Add(new FloatField("Ratio") { name = "float-field", value = 0f });
            CoverageHost.Add(new DoubleField("Double Ratio") { name = "double-field", value = 0d });
            CoverageHost.Add(new EnumField("Mode", DemoMode.Basic) { name = "mode-enum" });
            CoverageHost.Add(new EnumFlagsField("Permissions", DemoPermissions.None) { name = "permissions-flags" });
            CoverageHost.Add(new MaskField { name = "feature-mask", label = "Feature Mask", choices = new List<string> { "One", "Two", "Three" }, value = 0 });
            CoverageHost.Add(new LayerMaskField("Layer Mask") { name = "layer-mask-selector", value = 0 });
            CoverageHost.Add(new PopupField<string>("Quick Popup", new List<string> { "North", "South", "West" }, 0) { name = "quick-popup" });
            CoverageHost.Add(new TagField("Tag") { name = "tag-selector" });
            CoverageHost.Add(new LayerField("Layer") { name = "layer-selector", value = 0 });
            CoverageHost.Add(new RadioButton("Single Radio") { name = "single-radio", value = false });
            CoverageHost.Add(new RadioButtonGroup("Radio Group", new List<string> { "Red", "Green", "Blue" }) { name = "radio-group", value = 0 });
            CoverageHost.Add(new Slider("Volume", 0f, 10f) { name = "volume-slider", value = 0f });
            CoverageHost.Add(new SliderInt("Level", 0, 5) { name = "level-slider", value = 0 });
            CoverageHost.Add(new MinMaxSlider("Range", 10f, 20f, 0f, 100f) { name = "range-slider" });
            CoverageHost.Add(new UnsignedIntegerField("Max Items") { name = "max-items", value = 0u });
            CoverageHost.Add(new UnsignedLongField("Total Bytes") { name = "total-bytes", value = 0ul });
            CoverageHost.Add(new ColorField("Accent") { name = "accent-color", value = Color.white, showEyeDropper = false });
            CoverageHost.Add(new Vector2Field("Anchor") { name = "anchor-vector2", value = Vector2.zero });
            CoverageHost.Add(new Vector2IntField("Cell") { name = "cell-vector2int", value = Vector2Int.zero });
            CoverageHost.Add(new Vector3Field("Offset") { name = "offset-vector3", value = Vector3.zero });
            CoverageHost.Add(new Vector3IntField("Grid Position") { name = "grid-position-vector3int", value = Vector3Int.zero });
            CoverageHost.Add(new Vector4Field("Quaternion") { name = "quaternion-vector4", value = Vector4.zero });
            CoverageHost.Add(new RectField("Viewport") { name = "viewport-rect", value = new Rect(0f, 0f, 10f, 10f) });
            CoverageHost.Add(new RectIntField("Grid") { name = "grid-rectint", value = new RectInt(0, 0, 1, 1) });
            CoverageHost.Add(new BoundsField("Volume") { name = "volume-bounds", value = new Bounds(Vector3.zero, Vector3.one) });
            CoverageHost.Add(new BoundsIntField("Voxel Volume") { name = "voxel-boundsint", value = new BoundsInt(Vector3Int.zero, Vector3Int.one) });
            CoverageHost.Add(new Hash128Field("Content Hash") { name = "content-hash", value = new Hash128(0u, 0u, 0u, 0u) });
            CoverageHost.Add(new ObjectField("Template Asset") { name = "template-asset", objectType = typeof(VisualTreeAsset), allowSceneObjects = false, value = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Examples/Uxml/SampleInteractionWindow.uxml") });
            CoverageHost.Add(new CurveField("Speed Curve") { name = "speed-curve", value = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 2f)) });
            CoverageHost.Add(new GradientField("Ramp") { name = "ramp-gradient", value = CreateGradient() });
            CoverageHost.Add(new ProgressBar { name = "progress-bar", title = "Progress", lowValue = 0f, highValue = 100f, value = 65f });
            CoverageHost.Add(new Image { name = "preview-image", scaleMode = ScaleMode.ScaleToFit });
            CoverageHost.Add(new HelpBox("Helpful message", HelpBoxMessageType.Info) { name = "help-box" });
            CoverageHost.Add(new Box { name = "display-box" });
            CoverageHost.Add(new GroupBox("Settings Group") { name = "settings-group" });
            CoverageHost.Add(propertyField);
            CoverageHost.Add(inspector);

            CoverageHost.schedule.Execute(() =>
            {
                TextField propertyText = propertyField.Q<TextField>();
                if (propertyText != null) propertyText.name = "property-title-input";
                TextField inspectorText = inspector.Q<TextField>();
                if (inspectorText != null) inspectorText.name = "inspector-title-input";
                IntegerField inspectorCount = inspector.Q<IntegerField>();
                if (inspectorCount != null) inspectorCount.name = "inspector-count-input";
                Toggle inspectorToggle = inspector.Q<Toggle>();
                if (inspectorToggle != null) inspectorToggle.name = "inspector-enabled-toggle";
            }).ExecuteLater(0);
        }

        private static Gradient CreateGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.green, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.5f, 1f) });
            return gradient;
        }
    }

    public sealed class ExampleCoverageCollectionsWindow : ExampleCoverageWindowBase
    {
        private static readonly BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private readonly List<string> _items = new List<string> { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };
        private readonly List<string> _planets = new List<string> { "Mercury", "Venus", "Earth", "Mars", "Jupiter" };
        private readonly List<string> _sceneTreeItems = new List<string> { "Display", "Audio", "Network" };
        private Label _collectionStatus;
        private Label _splitStatus;
        private Label _orderStatus;
        private TwoPaneSplitView _split;
        private VisualElement _planetList;
        private VisualElement _sceneTree;

        protected override string WindowTitle => "Example Coverage Collections";

        protected override void OnEnable()
        {
            base.OnEnable();
            EditorApplication.update -= RefreshStatus;
            EditorApplication.update += RefreshStatus;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RefreshStatus;
        }

        protected override void AfterBuild()
        {
            ResetHost("Coverage Collections");

            var listView = new ListView { name = "item-list", itemsSource = _items, selectionType = SelectionType.Multiple, reorderable = true, fixedItemHeight = 20, style = { height = 120 } };
            listView.makeItem = () => new Label();
            listView.bindItem = (element, index) => ((Label)element).text = _items[index];
            listView.Rebuild();
            Label listStatus = new Label("List: none") { name = "list-selection-status" };
            _orderStatus = new Label($"Order: {string.Join(",", _items)}") { name = "list-order-status" };
            listView.selectionChanged += _ => listStatus.text = listView.selectedIndices.Any() ? $"List: {string.Join(",", listView.selectedIndices.OrderBy(x => x))}" : "List: none";

            var treeView = new TreeView { name = "navigation-tree", selectionType = SelectionType.Single, fixedItemHeight = 20, style = { height = 120 } };
            treeView.SetRootItems(new List<TreeViewItemData<string>>
            {
                new TreeViewItemData<string>(100, _sceneTreeItems[0]),
                new TreeViewItemData<string>(120, _sceneTreeItems[1]),
                new TreeViewItemData<string>(210, _sceneTreeItems[2]),
            });
            treeView.makeItem = () => new Label();
            treeView.bindItem = (element, index) => ((Label)element).text = treeView.GetItemDataForIndex<string>(index);
            treeView.ExpandRootItems();
            Label treeStatus = new Label("Tree: none") { name = "tree-selection-status" };
            treeView.selectionChanged += _ => treeStatus.text = treeView.selectedIndex >= 0 ? $"Tree: {treeView.GetItemDataForIndex<string>(treeView.selectedIndex)}" : "Tree: none";

            _planetList = CreateMultiColumnView("UnityEngine.UIElements.MultiColumnListView", "planet-list", _planets, index => _planets[index], onCustomSort: (view, col, dir) =>
            {
                object sortingMode = ReadMember(view, "sortingMode", "SortingMode");
                bool isCustom = sortingMode?.ToString() == "Custom";
                if (isCustom)
                {
                    _collectionStatus.text = $"CustomSort: {col}:{dir}";
                }
            });
            _sceneTree = CreateMultiColumnTree();
            _collectionStatus = new Label("Columns: idle") { name = "collection-status" };

            _split = new TwoPaneSplitView(0, 180, TwoPaneSplitViewOrientation.Horizontal) { name = "workspace-split" };
            _split.style.height = 120;
            _split.Add(new VisualElement { name = "left-pane" });
            _split.Add(new VisualElement { name = "right-pane" });
            _split.Q<VisualElement>("left-pane").Add(new Label("Inspector"));
            _split.Q<VisualElement>("right-pane").Add(new Label("Preview"));
            _splitStatus = new Label("Split: 180") { name = "split-status" };

            Label scrollerStatus = new Label("Scroller: 10") { name = "scroller-status" };
            var scroller = new Scroller(0f, 100f, value => scrollerStatus.text = $"Scroller: {value:0.###}", SliderDirection.Vertical) { name = "standalone-scroller", value = 10f };
            scroller.style.height = 100;

            var tabs = new TabView { name = "settings-tabs" };
            var tabGeneral = CreateClosableTab("General", "tab-general");
            var tabAdvanced = CreateClosableTab("Advanced", "tab-advanced");
            var tabAbout = CreateClosableTab("About", "tab-about");
            tabs.Add(tabGeneral);
            tabs.Add(tabAdvanced);
            tabs.Add(tabAbout);
            tabs.activeTab = tabs.Q<Tab>("tab-general");
            Label tabStatus = new Label("Tab: General") { name = "tab-selection-status" };
            Label tabClosedStatus = new Label("Closed: none") { name = "tab-closed-status" };
            tabs.activeTabChanged += (_, to) => tabStatus.text = to == null ? "Tab: none" : $"Tab: {to.label}";
            tabs.tabClosed += (tab, idx) => tabClosedStatus.text = $"Closed: {GetTabLabel(tab)}";

            CoverageHost.Add(listView);
            CoverageHost.Add(listStatus);
            CoverageHost.Add(_orderStatus);
            CoverageHost.Add(treeView);
            CoverageHost.Add(treeStatus);
            if (_planetList != null) CoverageHost.Add(_planetList);
            if (_sceneTree != null) CoverageHost.Add(_sceneTree);
            CoverageHost.Add(_collectionStatus);
            CoverageHost.Add(_split);
            CoverageHost.Add(_splitStatus);
            CoverageHost.Add(scroller);
            CoverageHost.Add(scrollerStatus);
            CoverageHost.Add(tabs);
            CoverageHost.Add(tabStatus);
            CoverageHost.Add(tabClosedStatus);
        }

        private void RefreshStatus()
        {
            if (_orderStatus != null) _orderStatus.text = $"Order: {string.Join(",", _items)}";
            if (_split != null) _splitStatus.text = $"Split: {ReadMember(_split, "fixedPaneInitialDimension", "FixedPaneInitialDimension")}";
            if (_planetList != null && _sceneTree != null)
            {
                if (_collectionStatus != null && _collectionStatus.text != null && _collectionStatus.text.StartsWith("CustomSort:"))
                {
                    return;
                }
                object sort = GetIndexedMember(ReadMember(_planetList, "sortColumnDescriptions", "SortColumnDescriptions"), 0);
                object firstColumn = GetIndexedMember(ReadMember(_sceneTree, "columns", "Columns"), 0);
                _collectionStatus.text = $"Columns: sort={ReadMember(sort, "columnName", "ColumnName")}:{ReadMember(sort, "direction", "Direction")}; tree-width={ReadMember(firstColumn, "width", "Width")}";
            }
        }

        private VisualElement CreateMultiColumnView(string typeName, string name, IList<string> items, Func<int, string> resolver, Action<VisualElement, string, SortDirection> onCustomSort = null)
        {
            Type viewType = typeof(VisualElement).Assembly.GetType(typeName);
            if (viewType == null || !(Activator.CreateInstance(viewType) is VisualElement view)) return null;
            view.name = name;
            view.style.height = 120;
            SafeSetValue(viewType, view, "itemsSource", items);
            SafeSetValue(viewType, view, "selectionType", SelectionType.Multiple);
            SafeSetValue(viewType, view, "fixedItemHeight", 20f);
            AddTextColumn(view, resolver, name == "planet-list" ? "Planet" : "Node");

            if (onCustomSort != null)
            {
                PropertyInfo sortDescProp = viewType.GetProperty("sortColumnDescriptions", PublicInstance);
                System.Action callback = () =>
                {
                    object sortDesc = sortDescProp?.GetValue(view);
                    object first = GetIndexedMember(sortDesc, 0);
                    string colName = ReadMember(first, "columnName", "ColumnName")?.ToString() ?? string.Empty;
                    object dirObj = ReadMember(first, "direction", "Direction");
                    SortDirection direction = dirObj is SortDirection sd ? sd : SortDirection.Ascending;
                    onCustomSort(view, colName, direction);
                };

                PropertyInfo customSortProp = viewType.GetProperty("customSortingCallback", PublicInstance);
                if (customSortProp != null && customSortProp.CanWrite)
                {
                    var paramExpr = System.Linq.Expressions.Expression.Parameter(viewType, "v");
                    var callExpr = System.Linq.Expressions.Expression.Call(
                        System.Linq.Expressions.Expression.Constant(callback.Target),
                        callback.Method);
                    var lambdaExpr = System.Linq.Expressions.Expression.Lambda(callExpr, paramExpr);
                    object compiled = lambdaExpr.Compile();
                    customSortProp.SetValue(view, compiled);
                }
                else
                {
                    EventInfo sortChangedEvent = viewType.GetEvent("columnSortingChanged", PublicInstance);
                    if (sortChangedEvent == null)
                    {
                        sortChangedEvent = viewType.GetEvent("columnSortingChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    sortChangedEvent?.AddEventHandler(view, callback);
                }
            }

            viewType.GetMethod("Rebuild", PublicInstance, null, Type.EmptyTypes, null)?.Invoke(view, null);
            return view;
        }

        private VisualElement CreateMultiColumnTree()
        {
            return CreateMultiColumnView(
                "UnityEngine.UIElements.MultiColumnListView",
                "scene-tree",
                _sceneTreeItems,
                index => index >= 0 && index < _sceneTreeItems.Count ? _sceneTreeItems[index] : string.Empty);
        }


        private static void AddTextColumn(VisualElement collectionView, Func<int, string> resolver, string title)
        {
            Type columnType = collectionView.GetType().Assembly.GetType("UnityEngine.UIElements.Column");
            object columns = collectionView.GetType().GetProperty("columns", PublicInstance)?.GetValue(collectionView);
            if (columnType == null || columns == null) return;
            object column = Activator.CreateInstance(columnType);
            SafeSetValue(columnType, column, "title", title);
            SafeSetValue(columnType, column, "name", title.ToLowerInvariant());
            SafeSetValue(columnType, column, "width", 180f);
            SafeSetValue(columnType, column, "makeCell", (Func<VisualElement>)(() => new Label()));
            SafeSetValue(columnType, column, "bindCell", (Action<VisualElement, int>)((element, index) => { if (element is Label label) label.text = resolver(index); }));
            columns.GetType().GetMethod("Add", PublicInstance, null, new[] { columnType }, null)?.Invoke(columns, new[] { column });
        }

        private static void SafeSetValue(Type targetType, object target, string propertyName, object value)
        {
            PropertyInfo property = targetType.GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite) return;
            try
            {
                Type propertyType = property.PropertyType;
                object converted = value;
                if (value != null && !propertyType.IsInstanceOfType(value))
                {
                    if (propertyType == typeof(float) && value is double d) converted = (float)d;
                    else if (propertyType == typeof(double) && value is float f) converted = (double)f;
                    else if (propertyType == typeof(int) && (value is float fv || value is double dv)) converted = Convert.ToInt32(value);
                    else if (propertyType == typeof(Length) && (value is float fl || value is double dl)) converted = new Length(Convert.ToSingle(value), LengthUnit.Pixel);
                    else if (propertyType == typeof(string) && value != null) converted = value.ToString();
                    else converted = Convert.ChangeType(value, propertyType);
                }
                property.SetValue(target, converted);
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[ExampleCoverageCollectionsWindow] Failed to set {propertyName} on {targetType.Name}: {ex.Message}");
            }
        }

        private static object ReadMember(object target, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = target?.GetType().GetProperty(name, PublicInstance);
                if (property != null) return property.GetValue(target);
            }
            return null;
        }

        private static object GetIndexedMember(object target, int index)
        {
            if (target is System.Collections.IList list) return index >= 0 && index < list.Count ? list[index] : null;
            PropertyInfo count = target?.GetType().GetProperty("Count", PublicInstance);
            PropertyInfo item = target?.GetType().GetProperty("Item", PublicInstance, null, null, new[] { typeof(int) }, null);
            if (count == null || item == null) return null;
            int total = (int)count.GetValue(target);
            return index >= 0 && index < total ? item.GetValue(target, new object[] { index }) : null;
        }

        private static Tab CreateClosableTab(string label, string name)
        {
            var tab = new Tab(label) { name = name };
            var closableProp = typeof(Tab).GetProperty("closable", PublicInstance);
            if (closableProp != null && closableProp.CanWrite)
            {
                closableProp.SetValue(tab, true);
            }
            return tab;
        }

        private static string GetTabLabel(Tab tab)
        {
            if (tab == null) return string.Empty;
            var labelProp = typeof(Tab).GetProperty("label", PublicInstance);
            if (labelProp?.CanRead == true)
            {
                return labelProp.GetValue(tab)?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
    }

    public sealed class ExampleCoverageInputWindow : ExampleCoverageWindowBase
    {
        [MenuItem("upilot/UIFlow/Examples/Coverage Input Window")]
        public static void Open()
        {
            ExampleCoverageInputWindow window = GetWindow<ExampleCoverageInputWindow>();
            window.titleContent = new GUIContent("Example Coverage Input");
            window.minSize = new Vector2(420f, 800f);
            window.Show();
        }

        protected override string WindowTitle => "Example Coverage Input";

        protected override void AfterBuild()
        {
            ResetHost("Coverage Input");

            Label toolbarStatus = new Label("Toolbar: idle") { name = "toolbar-status" };
            Label gestureStatus = new Label("Gesture: idle") { name = "gesture-status" };
            Label keyboardStatus = new Label("Key: idle") { name = "keyboard-status" };
            Label commandStatus = new Label("Command: idle") { name = "command-status" };

            var toolbar = new Toolbar { name = "main-toolbar" };
            toolbar.Add(new ToolbarButton(() => toolbarStatus.text = "Toolbar: button") { name = "toolbar-run", text = "Run" });
            var liveToggle = new ToolbarToggle { name = "toolbar-live-toggle", text = "Live", value = false };
            liveToggle.RegisterValueChangedCallback(evt => toolbarStatus.text = $"Toolbar: live={evt.newValue.ToString().ToLowerInvariant()}");
            toolbar.Add(liveToggle);
            var search = new ToolbarSearchField { name = "toolbar-search", value = string.Empty };
            search.RegisterValueChangedCallback(evt => toolbarStatus.text = $"Toolbar: search={evt.newValue}");
            toolbar.Add(search);
            VisualElement toolbarPopupSearch = CreateToolbarPopupSearchField(toolbarStatus);
            if (toolbarPopupSearch != null)
            {
                toolbar.Add(toolbarPopupSearch);
            }
            var menu = new ToolbarMenu { name = "toolbar-menu", text = "Actions" };
            menu.menu.AppendAction("Refresh", _ => toolbarStatus.text = "Toolbar: refresh");
            menu.menu.AppendAction("Reset", _ => toolbarStatus.text = "Toolbar: reset");
            menu.menu.AppendAction("Disabled", _ => toolbarStatus.text = "Toolbar: disabled", DropdownMenuAction.AlwaysDisabled);
            toolbar.Add(menu);
            VisualElement breadcrumbs = new VisualElement { name = "toolbar-breadcrumbs" };
            breadcrumbs.Add(new Button(() => toolbarStatus.text = "Toolbar: breadcrumb-home") { name = "breadcrumb-item-0", text = "Home" });
            breadcrumbs.Add(new Button(() => toolbarStatus.text = "Toolbar: breadcrumb-settings") { name = "breadcrumb-item-1", text = "Settings" });
            toolbar.Add(breadcrumbs);

            VisualElement clickTarget = new Button { name = "gesture-click-target", text = "Click Target" };
            clickTarget.RegisterCallback<MouseUpEvent>(evt => gestureStatus.text = $"Click: button={(int)evt.button}; modifiers={evt.modifiers}");
            VisualElement doubleClickTarget = new VisualElement { name = "gesture-double-target" };
            doubleClickTarget.style.height = 32;
            doubleClickTarget.Add(new Label("Double Target"));
            int doubleCount = 0;
            doubleClickTarget.RegisterCallback<MouseUpEvent>(_ =>
            {
                doubleCount++;
                gestureStatus.text = $"Double: {doubleCount}";
            });
            var repeatButton = new RepeatButton(() => gestureStatus.text = "Repeat: fired", 250L, 30L) { name = "repeat-button", text = "Repeat Button" };
            repeatButton.RegisterCallback<MouseUpEvent>(_ => gestureStatus.text = "Repeat: clicked");
            VisualElement hoverTarget = new VisualElement { name = "gesture-hover-target" };
            hoverTarget.style.height = 28;
            hoverTarget.Add(new Label("Hover Target"));
            hoverTarget.RegisterCallback<MouseMoveEvent>(evt => gestureStatus.text = $"Hover: modifiers={evt.modifiers}");
            VisualElement dragTarget = new VisualElement { name = "gesture-drag-target" };
            dragTarget.style.height = 32;
            dragTarget.Add(new Label("Drag Target"));
            bool dragActive = false;
            dragTarget.RegisterCallback<MouseDownEvent>(evt => { dragActive = true; gestureStatus.text = $"Drag: started button={(int)evt.button}; modifiers={evt.modifiers}"; });
            dragTarget.RegisterCallback<PointerDownEvent>(evt => { dragActive = true; gestureStatus.text = $"Drag: started button={(int)evt.button}; modifiers={evt.modifiers}"; });
            dragTarget.RegisterCallback<MouseMoveEvent>(evt => { if (dragActive) gestureStatus.text = $"Drag: moving modifiers={evt.modifiers}"; });
            dragTarget.RegisterCallback<PointerMoveEvent>(evt => { if (dragActive) gestureStatus.text = $"Drag: moving modifiers={evt.modifiers}"; });
            dragTarget.RegisterCallback<MouseUpEvent>(evt => { if (dragActive) { dragActive = false; gestureStatus.text = $"Drag: completed button={(int)evt.button}; modifiers={evt.modifiers}"; } });
            dragTarget.RegisterCallback<PointerUpEvent>(evt => { if (dragActive) { dragActive = false; gestureStatus.text = $"Drag: completed button={(int)evt.button}; modifiers={evt.modifiers}"; } });

            TextField keyboardInput = new TextField("Keyboard Input") { name = "keyboard-input", value = string.Empty };
            keyboardInput.RegisterCallback<KeyDownEvent>(evt => keyboardStatus.text = $"Key: {evt.keyCode}");
            keyboardInput.RegisterValueChangedCallback(evt => keyboardStatus.text = $"Input: {evt.newValue}");

            TextField commandInput = new TextField("Command Input") { name = "command-input", value = string.Empty };
            // Use TrickleDown.TrickleDown to ensure these callbacks fire before TextField's internal
            // command handling stops propagation.
            commandInput.RegisterCallback<ValidateCommandEvent>(evt => { commandStatus.text = $"Validate: {evt.commandName}"; evt.StopPropagation(); }, TrickleDown.TrickleDown);
            commandInput.RegisterCallback<ExecuteCommandEvent>(evt =>
            {
                var supported = new System.Collections.Generic.HashSet<string> { "SelectAll", "Copy", "Paste", "Cut", "Delete", "Undo", "Redo" };
                if (!supported.Contains(evt.commandName))
                {
                    throw new System.InvalidOperationException($"Unsupported command: {evt.commandName}");
                }
                commandStatus.text = $"Execute: {evt.commandName}";
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);

            VisualElement contextTarget = new VisualElement { name = "context-target" };
            contextTarget.style.height = 28;
            contextTarget.Add(new Label("Context Target"));
            contextTarget.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Copy", _ => commandStatus.text = "Context: Copy");
                evt.menu.AppendAction("Paste", _ => commandStatus.text = "Context: Paste", DropdownMenuAction.AlwaysDisabled);
            }));

            Button enabledButton = new Button { name = "enabled-button", text = "Enabled Button" };
            Button disabledButton = new Button { name = "disabled-button", text = "Disabled Button" };
            disabledButton.SetEnabled(false);

            CoverageHost.Add(toolbar);
            CoverageHost.Add(toolbarStatus);
            CoverageHost.Add(clickTarget);
            CoverageHost.Add(doubleClickTarget);
            CoverageHost.Add(repeatButton);
            CoverageHost.Add(hoverTarget);
            CoverageHost.Add(dragTarget);
            CoverageHost.Add(gestureStatus);
            CoverageHost.Add(keyboardInput);
            CoverageHost.Add(keyboardStatus);
            CoverageHost.Add(commandInput);
            CoverageHost.Add(contextTarget);
            CoverageHost.Add(commandStatus);
            CoverageHost.Add(enabledButton);
            CoverageHost.Add(disabledButton);
        }

        private static VisualElement CreateToolbarPopupSearchField(Label toolbarStatus)
        {
            try
            {
                Type type = typeof(ToolbarSearchField).Assembly.GetType("UnityEditor.UIElements.ToolbarPopupSearchField");
                if (type == null || !(Activator.CreateInstance(type) is VisualElement element))
                {
                    return null;
                }

                element.name = "toolbar-popup-search";
                PropertyInfo valueProperty = type.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProperty != null && valueProperty.CanWrite)
                {
                    valueProperty.SetValue(element, string.Empty);
                }

                TextField input = element.Q<TextField>();
                if (input != null)
                {
                    input.name = "toolbar-popup-search-input";
                    input.RegisterValueChangedCallback(evt => toolbarStatus.text = $"Toolbar: popup-search={evt.newValue}");
                }

                return element;
            }
            catch
            {
                return null;
            }
        }
    }
}
