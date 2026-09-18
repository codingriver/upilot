// -----------------------------------------------------------------------
// UPilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotSafeWindowProbe : EditorWindow
    {
        internal const string Title = "UPilot Safe Probe";
        internal static readonly string FullTypeName = typeof(UPilotSafeWindowProbe).FullName;
        [SerializeField] private string _lifecycleKey;
        [SerializeField] private int _enableOrdinal;
        [SerializeField] private bool _reloadFixtureArmed;

        // This hook is deliberately local to the self-owned probe. It lets the focused
        // EditMode fixture exercise OnEnable diagnostics without intercepting arbitrary
        // EditorWindow lifecycle callbacks.
        internal static bool FailOnEnableForTests { get; set; }
        internal string LifecycleKey => _lifecycleKey;
        internal int EnableOrdinal => _enableOrdinal;
        internal bool ReloadFixtureArmed => _reloadFixtureArmed;

        internal static bool IsAllowedTypeName(string typeName) =>
            string.Equals(typeName, FullTypeName, StringComparison.Ordinal);

        internal void ArmReloadFixtureForTests()
        {
            EnsureLifecycleKey();
            _reloadFixtureArmed = true;
        }

        private void OnEnable()
        {
            try
            {
                EnsureLifecycleKey();
                var rebuiltAfterReload = _reloadFixtureArmed && _enableOrdinal > 0;
                _enableOrdinal++;
                if (FailOnEnableForTests)
                    throw new InvalidOperationException("Deterministic UPilotSafeWindowProbe OnEnable test failure.");

                minSize = new Vector2(240, 120);
                UPilotWindowHistory.RecordSafeProbeEnabled(this, _lifecycleKey, rebuiltAfterReload);
            }
            catch (Exception ex)
            {
                // Do not synthesize a closed-or-lost event or a causal failure reason:
                // this hook observed only its own initialization failure.
                UPilotWindowHistory.RecordObservedGap("window_lifecycle_enable_failed", ex.GetType().Name);
            }
        }

        private void EnsureLifecycleKey()
        {
            if (string.IsNullOrEmpty(_lifecycleKey))
                _lifecycleKey = Guid.NewGuid().ToString("N");
        }
    }
}
