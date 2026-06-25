using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class AdvancedControlsWindow : EditorWindow
    {
        [Serializable]
        private sealed class AdvancedControlsData : ScriptableObject
        {
            public string inspectorTitle = "Draft";
            public int inspectorCount = 2;
            public bool inspectorEnabled = true;
        }

        private enum DemoMode
        {
            Basic,
            Advanced,
            Expert,
        }

        [System.Flags]
        private enum DemoPermissions
        {
            None = 0,
            Read = 1,
            Write = 2,
            Execute = 4,
        }

        private static readonly string SampleUxmlAssetPath = "Assets/Examples/Uxml/SampleInteractionWindow.uxml";
        private readonly List<string> _multiColumnItems = new List<string> { "Mercury", "Venus", "Earth", "Mars", "Jupiter" };
        private readonly List<string> _items = new List<string> { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };
        private readonly List<TreeViewItemData<string>> _treeItems = new List<TreeViewItemData<string>>
        {
            new TreeViewItemData<string>(100, "Root A", new List<TreeViewItemData<string>>
            {
                new TreeViewItemData<string>(110, "Leaf A1"),
                new TreeViewItemData<string>(120, "Leaf A2"),
            }),
            new TreeViewItemData<string>(200, "Root B", new List<TreeViewItemData<string>>
            {
                new TreeViewItemData<string>(210, "Leaf B1"),
            }),
        };
        private static readonly BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private AdvancedControlsData _inspectorData;

        public void CreateGUI()
        {
            BuildUi();
        }

        private void OnEnable()
        {
            BuildUi();
        }

        public void BuildUi()
        {
            EnsureInspectorData();
            rootVisualElement.Clear();
            rootVisualElement.style.paddingLeft = 8;
            rootVisualElement.style.paddingTop = 8;
            rootVisualElement.style.paddingRight = 8;
            rootVisualElement.style.paddingBottom = 8;
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var dropdown = new DropdownField("Choice", new List<string> { "Alpha", "Beta", "Gamma" }, 0)
            {
                name = "choice-dropdown",
            };

            var foldout = new Foldout
            {
                name = "settings-foldout",
                text = "Advanced Settings",
                value = false,
            };
            foldout.Add(new Label("Nested content"));

            var slider = new Slider("Volume", 0f, 10f)
            {
                name = "volume-slider",
                value = 0f,
            };

            var sliderInt = new SliderInt("Level", 0, 5)
            {
                name = "level-slider",
                value = 0,
            };

            var range = new MinMaxSlider("Range", 10f, 20f, 0f, 100f)
            {
                name = "range-slider",
            };

            var toggle = new Toggle("Enabled")
            {
                name = "enabled-toggle",
                value = false,
            };

            var enumField = new EnumField("Mode", DemoMode.Basic)
            {
                name = "mode-enum",
            };

            var enumFlagsField = new EnumFlagsField("Permissions", DemoPermissions.None)
            {
                name = "permissions-flags",
            };

            var maskField = new MaskField
            {
                name = "feature-mask",
                label = "Feature Mask",
                choices = new List<string> { "One", "Two", "Three" },
                value = 0,
            };

            var unsignedInteger = new UnsignedIntegerField("Max Items")
            {
                name = "max-items",
                value = 0u,
            };

            var unsignedLong = new UnsignedLongField("Total Bytes")
            {
                name = "total-bytes",
                value = 0ul,
            };

            var color = new ColorField("Tint")
            {
                name = "accent-color",
                value = Color.white,
                showEyeDropper = false,
            };

            var vector3 = new Vector3Field("Offset")
            {
                name = "offset-vector3",
                value = Vector3.zero,
            };

            var vector2 = new Vector2Field("Anchor")
            {
                name = "anchor-vector2",
                value = Vector2.zero,
            };

            var vector2Int = new Vector2IntField("Cell")
            {
                name = "cell-vector2int",
                value = Vector2Int.zero,
            };

            var vector4 = new Vector4Field("Quaternion")
            {
                name = "quaternion-vector4",
                value = Vector4.zero,
            };

            var rect = new RectField("Viewport")
            {
                name = "viewport-rect",
                value = new Rect(0f, 0f, 10f, 10f),
            };

            var rectInt = new RectIntField("Grid")
            {
                name = "grid-rectint",
                value = new RectInt(0, 0, 1, 1),
            };

            var bounds = new BoundsField("Volume")
            {
                name = "volume-bounds",
                value = new Bounds(Vector3.zero, Vector3.one),
            };

            var boundsInt = new BoundsIntField("Voxel Volume")
            {
                name = "voxel-boundsint",
                value = new BoundsInt(Vector3Int.zero, Vector3Int.one),
            };

            var hash128Field = new Hash128Field("Content Hash")
            {
                name = "content-hash",
                value = new Hash128(0u, 0u, 0u, 0u),
            };

            var popupField = new PopupField<string>("Quick Popup", new List<string> { "North", "South", "West" }, 0)
            {
                name = "quick-popup",
            };

            var tagField = new TagField("Tag")
            {
                name = "tag-selector",
            };

            var layerField = new LayerField("Layer")
            {
                name = "layer-selector",
                value = 0,
            };

            var objectField = new ObjectField("Template Asset")
            {
                name = "template-asset",
                objectType = typeof(VisualTreeAsset),
                allowSceneObjects = false,
            };

            VisualTreeAsset templateAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SampleUxmlAssetPath);
            if (templateAsset != null)
            {
                objectField.value = templateAsset;
            }

            var curveField = new CurveField("Speed Curve")
            {
                name = "speed-curve",
                value = new AnimationCurve(
                    new Keyframe(0f, 0f, 1f, 1f),
                    new Keyframe(1f, 2f, 0f, 0f)),
            };

            var gradientField = new GradientField("Ramp")
            {
                name = "ramp-gradient",
                value = CreateDefaultGradient(),
            };

            SerializedObject inspectorSerializedObject = new SerializedObject(_inspectorData);
            var propertyField = new PropertyField(inspectorSerializedObject.FindProperty("inspectorTitle"), "Property Title")
            {
                name = "property-title-field",
            };
            propertyField.Bind(inspectorSerializedObject);

            VisualElement inspectorElement = CreateInspectorElement(inspectorSerializedObject);

            var toolbarStatus = new Label("Toolbar: idle")
            {
                name = "toolbar-status",
            };

            var toolbar = new Toolbar
            {
                name = "main-toolbar",
            };

            var toolbarButton = new ToolbarButton(() => toolbarStatus.text = "Toolbar: button")
            {
                name = "toolbar-run",
                text = "Run",
            };

            var toolbarToggle = new ToolbarToggle
            {
                name = "toolbar-live-toggle",
                text = "Live",
                value = false,
            };
            toolbarToggle.RegisterValueChangedCallback(evt => toolbarStatus.text = $"Toolbar: live={evt.newValue.ToString().ToLowerInvariant()}");

            var toolbarSearch = new ToolbarSearchField
            {
                name = "toolbar-search",
                value = string.Empty,
            };
            toolbarSearch.RegisterValueChangedCallback(evt => toolbarStatus.text = $"Toolbar: search={evt.newValue}");

            var toolbarMenu = new ToolbarMenu
            {
                name = "toolbar-menu",
                text = "Actions",
            };
            toolbarMenu.menu.AppendAction("Refresh", _ => toolbarStatus.text = "Toolbar: refresh");
            toolbarMenu.menu.AppendAction("Reset", _ => toolbarStatus.text = "Toolbar: reset");

            VisualElement toolbarPopupSearch = CreateToolbarPopupSearchField();
            VisualElement toolbarBreadcrumbs = CreateToolbarBreadcrumbs(toolbarStatus);

            var scrollerStatus = new Label("Scroller: 10")
            {
                name = "scroller-status",
            };

            var standaloneScroller = new Scroller(0f, 100f, value => scrollerStatus.text = $"Scroller: {value:0.###}", SliderDirection.Vertical)
            {
                name = "standalone-scroller",
                value = 10f,
            };
            standaloneScroller.style.height = 100;

            var listView = new ListView
            {
                name = "item-list",
                itemsSource = _items,
                selectionType = SelectionType.Multiple,
                fixedItemHeight = 20,
                style =
                {
                    height = 120,
                },
            };
            listView.makeItem = () => new Label();
            listView.bindItem = (element, index) => ((Label)element).text = _items[index];
            listView.Rebuild();

            var listStatus = new Label("List: none")
            {
                name = "list-selection-status",
            };
            listView.selectionChanged += _ =>
            {
                listStatus.text = listView.selectedIndex >= 0
                    ? $"List: {listView.selectedIndex}"
                    : "List: none";
            };

            var treeView = new TreeView
            {
                name = "navigation-tree",
                selectionType = SelectionType.Single,
                fixedItemHeight = 20,
                style =
                {
                    height = 140,
                },
            };
            treeView.SetRootItems(_treeItems);
            treeView.makeItem = () => new Label();
            treeView.bindItem = (element, index) => ((Label)element).text = treeView.GetItemDataForIndex<string>(index);
            treeView.ExpandRootItems();

            var treeStatus = new Label("Tree: none")
            {
                name = "tree-selection-status",
            };
            treeView.selectionChanged += _ =>
            {
                treeStatus.text = treeView.selectedIndex >= 0
                    ? $"Tree: {treeView.GetItemDataForIndex<string>(treeView.selectedIndex)}"
                    : "Tree: none";
            };

            VisualElement multiColumnListView = CreateMultiColumnListView();
            VisualElement multiColumnTreeView = CreateMultiColumnTreeView();

            var splitView = new TwoPaneSplitView(0, 180, TwoPaneSplitViewOrientation.Horizontal)
            {
                name = "workspace-split",
            };
            splitView.style.height = 120;
            splitView.style.flexGrow = 0;

            var leftPane = new VisualElement
            {
                name = "left-pane",
            };
            leftPane.style.minWidth = 120;
            leftPane.Add(new Label("Inspector"));

            var rightPane = new VisualElement
            {
                name = "right-pane",
            };
            rightPane.Add(new Label("Preview"));

            splitView.Add(leftPane);
            splitView.Add(rightPane);

            var tabView = new TabView
            {
                name = "settings-tabs",
            };
            var generalTab = new Tab("General")
            {
                name = "tab-general",
            };
            generalTab.Add(new Label("General Panel"));
            var advancedTab = new Tab("Advanced")
            {
                name = "tab-advanced",
            };
            advancedTab.Add(new Label("Advanced Panel"));
            var aboutTab = new Tab("About")
            {
                name = "tab-about",
            };
            aboutTab.Add(new Label("About Panel"));
            tabView.Add(generalTab);
            tabView.Add(advancedTab);
            tabView.Add(aboutTab);
            tabView.activeTab = generalTab;

            var tabStatus = new Label("Tab: General")
            {
                name = "tab-selection-status",
            };
            tabView.activeTabChanged += (_, to) =>
            {
                tabStatus.text = to == null ? "Tab: none" : $"Tab: {to.label}";
            };

            rootVisualElement.Add(dropdown);
            rootVisualElement.Add(foldout);
            rootVisualElement.Add(slider);
            rootVisualElement.Add(sliderInt);
            rootVisualElement.Add(range);
            rootVisualElement.Add(toggle);
            rootVisualElement.Add(enumField);
            rootVisualElement.Add(enumFlagsField);
            rootVisualElement.Add(maskField);
            rootVisualElement.Add(popupField);
            rootVisualElement.Add(tagField);
            rootVisualElement.Add(layerField);
            rootVisualElement.Add(objectField);
            rootVisualElement.Add(curveField);
            rootVisualElement.Add(gradientField);
            rootVisualElement.Add(propertyField);
            if (inspectorElement != null)
            {
                rootVisualElement.Add(inspectorElement);
            }
            rootVisualElement.Add(unsignedInteger);
            rootVisualElement.Add(unsignedLong);
            rootVisualElement.Add(color);
            rootVisualElement.Add(vector2);
            rootVisualElement.Add(vector2Int);
            rootVisualElement.Add(vector3);
            rootVisualElement.Add(vector4);
            rootVisualElement.Add(rect);
            rootVisualElement.Add(rectInt);
            rootVisualElement.Add(bounds);
            rootVisualElement.Add(boundsInt);
            rootVisualElement.Add(hash128Field);
            rootVisualElement.Add(standaloneScroller);
            rootVisualElement.Add(scrollerStatus);
            rootVisualElement.Add(listView);
            rootVisualElement.Add(listStatus);
            rootVisualElement.Add(treeView);
            rootVisualElement.Add(treeStatus);
            if (multiColumnListView != null)
            {
                rootVisualElement.Add(multiColumnListView);
            }

            if (multiColumnTreeView != null)
            {
                rootVisualElement.Add(multiColumnTreeView);
            }

            rootVisualElement.Add(splitView);
            toolbar.Add(toolbarButton);
            toolbar.Add(toolbarToggle);
            toolbar.Add(toolbarSearch);
            toolbar.Add(toolbarMenu);
            if (toolbarPopupSearch != null)
            {
                toolbar.Add(toolbarPopupSearch);
            }
            if (toolbarBreadcrumbs != null)
            {
                toolbar.Add(toolbarBreadcrumbs);
            }
            rootVisualElement.Add(toolbar);
            rootVisualElement.Add(toolbarStatus);
            rootVisualElement.Add(tabView);
            rootVisualElement.Add(tabStatus);
            rootVisualElement.schedule.Execute(() => NameGeneratedEditorChildren(propertyField, inspectorElement, toolbarBreadcrumbs, toolbarPopupSearch)).ExecuteLater(0);
        }

        private static Gradient CreateDefaultGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.red, 0f),
                    new GradientColorKey(Color.green, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                });
            return gradient;
        }

        private void EnsureInspectorData()
        {
            if (_inspectorData != null)
            {
                return;
            }

            _inspectorData = CreateInstance<AdvancedControlsData>();
            _inspectorData.hideFlags = HideFlags.HideAndDontSave;
        }

        private static VisualElement CreateInspectorElement(SerializedObject serializedObject)
        {
            try
            {
                return new InspectorElement(serializedObject)
                {
                    name = "settings-inspector",
                };
            }
            catch
            {
                return null;
            }
        }

        private static VisualElement CreateToolbarPopupSearchField()
        {
            try
            {
                Type type = typeof(ToolbarSearchField).Assembly.GetType("UnityEditor.UIElements.ToolbarPopupSearchField");
                if (type == null || !(Activator.CreateInstance(type) is VisualElement element))
                {
                    return null;
                }

                element.name = "toolbar-popup-search";
                PropertyInfo valueProperty = type.GetProperty("value", PublicInstance);
                valueProperty?.SetValue(element, string.Empty);
                return element;
            }
            catch
            {
                return null;
            }
        }

        private static VisualElement CreateToolbarBreadcrumbs(Label toolbarStatus)
        {
            try
            {
                Type type = typeof(ToolbarSearchField).Assembly.GetType("UnityEditor.UIElements.ToolbarBreadcrumbs");
                if (type == null || !(Activator.CreateInstance(type) is VisualElement element))
                {
                    return null;
                }

                element.name = "toolbar-breadcrumbs";
                InvokeMethodIfPresent(element, "PushItem", "Home", (Action)(() => toolbarStatus.text = "Toolbar: breadcrumb-home"));
                InvokeMethodIfPresent(element, "PushItem", "Settings", (Action)(() => toolbarStatus.text = "Toolbar: breadcrumb-settings"));

                if (element.childCount == 0)
                {
                    element.Add(new Button(() => toolbarStatus.text = "Toolbar: breadcrumb-home") { text = "Home" });
                    element.Add(new Button(() => toolbarStatus.text = "Toolbar: breadcrumb-settings") { text = "Settings" });
                }

                return element;
            }
            catch
            {
                return null;
            }
        }

        private static void NameGeneratedEditorChildren(VisualElement propertyField, VisualElement inspectorElement, VisualElement toolbarBreadcrumbs, VisualElement toolbarPopupSearch)
        {
            TextField propertyText = propertyField?.Q<TextField>();
            if (propertyText != null)
            {
                propertyText.name = "property-title-input";
            }

            if (inspectorElement != null)
            {
                TextField inspectorText = inspectorElement.Q<TextField>();
                if (inspectorText != null)
                {
                    inspectorText.name = "inspector-title-input";
                }

                IntegerField inspectorInt = inspectorElement.Q<IntegerField>();
                if (inspectorInt != null)
                {
                    inspectorInt.name = "inspector-count-input";
                }

                Toggle inspectorToggle = inspectorElement.Q<Toggle>();
                if (inspectorToggle != null)
                {
                    inspectorToggle.name = "inspector-enabled-toggle";
                }
            }

            if (toolbarBreadcrumbs != null)
            {
                int index = 0;
                foreach (Button button in toolbarBreadcrumbs.Query<Button>().ToList())
                {
                    button.name = $"breadcrumb-item-{index++}";
                }
            }

            if (toolbarPopupSearch != null)
            {
                TextField popupText = toolbarPopupSearch.Q<TextField>();
                if (popupText != null)
                {
                    popupText.name = "toolbar-popup-search-input";
                }
            }
        }

        private VisualElement CreateMultiColumnListView()
        {
            try
            {
                Type viewType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.MultiColumnListView");
                if (viewType == null)
                {
                    return null;
                }

                if (!(Activator.CreateInstance(viewType) is VisualElement view))
                {
                    return null;
                }

                view.name = "planet-list";
                view.style.height = 120;
                SetPropertyIfWritable(view, "itemsSource", _multiColumnItems);
                SetPropertyIfWritable(view, "selectionType", SelectionType.Multiple);
                SetPropertyIfWritable(view, "fixedItemHeight", 20f);
                TryAddTextColumn(view, "Planet", index => _multiColumnItems[index]);
                InvokeParameterlessIfPresent(view, "Rebuild");
                return view;
            }
            catch
            {
                return null;
            }
        }

        private VisualElement CreateMultiColumnTreeView()
        {
            try
            {
                Type viewType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.MultiColumnTreeView");
                if (viewType == null)
                {
                    return null;
                }

                if (!(Activator.CreateInstance(viewType) is VisualElement view))
                {
                    return null;
                }

                view.name = "scene-tree";
                view.style.height = 140;
                SetPropertyIfWritable(view, "selectionType", SelectionType.Single);
                SetPropertyIfWritable(view, "fixedItemHeight", 20f);
                InvokeMethodIfPresent(view, "SetRootItems", _treeItems);
                InvokeParameterlessIfPresent(view, "ExpandRootItems");
                TryAddTextColumn(view, "Node", index => TryGetTreeItemText(view, index));
                InvokeParameterlessIfPresent(view, "Rebuild");
                return view;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetTreeItemText(object treeView, int index)
        {
            MethodInfo method = null;
            foreach (MethodInfo candidate in treeView?.GetType().GetMethods(PublicInstance) ?? Array.Empty<MethodInfo>())
            {
                if (string.Equals(candidate.Name, "GetItemDataForIndex", StringComparison.Ordinal)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 1)
                {
                    method = candidate;
                    break;
                }
            }

            if (method == null)
            {
                return string.Empty;
            }

            try
            {
                object value = method.MakeGenericMethod(typeof(string)).Invoke(treeView, new object[] { index });
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryAddTextColumn(VisualElement collectionView, string title, Func<int, string> textResolver)
        {
            if (collectionView == null || textResolver == null)
            {
                return;
            }

            Type columnType = collectionView.GetType().Assembly.GetType("UnityEngine.UIElements.Column");
            PropertyInfo columnsProperty = collectionView.GetType().GetProperty("columns", PublicInstance);
            object columns = columnsProperty?.GetValue(collectionView);
            if (columnType == null || columns == null)
            {
                return;
            }

            object column = Activator.CreateInstance(columnType);
            SetPropertyIfWritable(column, "title", title);
            SetPropertyIfWritable(column, "name", title.ToLowerInvariant().Replace(" ", "-"));
            SetPropertyIfWritable(column, "width", 180f);

            PropertyInfo makeCellProperty = columnType.GetProperty("makeCell", PublicInstance);
            if (makeCellProperty != null && makeCellProperty.CanWrite && makeCellProperty.PropertyType.IsAssignableFrom(typeof(Func<VisualElement>)))
            {
                makeCellProperty.SetValue(column, (Func<VisualElement>)(() => new Label()));
            }

            PropertyInfo bindCellProperty = columnType.GetProperty("bindCell", PublicInstance);
            if (bindCellProperty != null && bindCellProperty.CanWrite && bindCellProperty.PropertyType.IsAssignableFrom(typeof(Action<VisualElement, int>)))
            {
                bindCellProperty.SetValue(column, (Action<VisualElement, int>)((element, index) =>
                {
                    if (element is Label label)
                    {
                        label.text = textResolver(index);
                    }
                }));
            }

            MethodInfo addMethod = columns.GetType().GetMethod("Add", PublicInstance, null, new[] { columnType }, null);
            addMethod?.Invoke(columns, new[] { column });
        }

        private static void SetPropertyIfWritable(object target, string propertyName, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite)
            {
                return;
            }

            try
            {
                property.SetValue(target, value);
            }
            catch
            {
            }
        }

        private static void InvokeParameterlessIfPresent(object target, string methodName)
        {
            target?.GetType().GetMethod(methodName, PublicInstance, null, Type.EmptyTypes, null)?.Invoke(target, null);
        }

        private static void InvokeMethodIfPresent(object target, string methodName, object argument)
        {
            MethodInfo[] methods = target?.GetType().GetMethods(PublicInstance) ?? Array.Empty<MethodInfo>();
            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || parameters.Length != 1)
                {
                    continue;
                }

                if (argument == null || parameters[0].ParameterType.IsInstanceOfType(argument))
                {
                    method.Invoke(target, new[] { argument });
                    return;
                }
            }
        }

        private static void InvokeMethodIfPresent(object target, string methodName, object firstArgument, object secondArgument)
        {
            MethodInfo[] methods = target?.GetType().GetMethods(PublicInstance) ?? Array.Empty<MethodInfo>();
            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || parameters.Length != 2)
                {
                    continue;
                }

                bool firstMatches = firstArgument == null || parameters[0].ParameterType.IsInstanceOfType(firstArgument) || parameters[0].ParameterType == typeof(string);
                bool secondMatches = secondArgument == null || parameters[1].ParameterType.IsInstanceOfType(secondArgument) || typeof(Delegate).IsAssignableFrom(parameters[1].ParameterType);
                if (!firstMatches || !secondMatches)
                {
                    continue;
                }

                try
                {
                    object callback = secondArgument;
                    if (callback != null
                        && typeof(Delegate).IsAssignableFrom(parameters[1].ParameterType)
                        && !parameters[1].ParameterType.IsInstanceOfType(callback))
                    {
                        callback = Delegate.CreateDelegate(parameters[1].ParameterType, ((Action)callback).Target, ((Action)callback).Method);
                    }

                    method.Invoke(target, new[] { firstArgument, callback });
                    return;
                }
                catch
                {
                }
            }
        }
    }
}
