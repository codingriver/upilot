using System;
using System.Collections.Generic;
using System.Linq;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationCaseDescriptorV1
    {
        public string id;
        public string displayName;
        public string[] beforeCaseIds = Array.Empty<string>();
        public string[] afterCaseIds = Array.Empty<string>();
        public bool mustBeLast;
    }

    [Serializable]
    public sealed class AutomationSuiteDescriptorV1
    {
        public string id;
        public string displayName;
        public string[] caseIds = Array.Empty<string>();
    }

    [Serializable]
    public sealed class AutomationCatalogDescriptorV1
    {
        public int version = 1;
        public AutomationCaseDescriptorV1[] cases = Array.Empty<AutomationCaseDescriptorV1>();
        public AutomationSuiteDescriptorV1[] suites = Array.Empty<AutomationSuiteDescriptorV1>();
    }

    [Serializable]
    public sealed class AutomationDiagnosticV1
    {
        public string code;
        public string severity = "error";
        public string message;
        public string subjectId;
        public int index = -1;
    }

    [Serializable]
    public sealed class AutomationCatalogValidationResultV1
    {
        public bool ok;
        public List<AutomationDiagnosticV1> diagnostics = new();
    }

    [Serializable]
    public sealed class AutomationSelectionRequestV1
    {
        public string suiteId;

        // null means not supplied. An empty array means explicitly supplied and is invalid.
        public string[] caseIds;
    }

    [Serializable]
    public sealed class AutomationSelectionResultV1
    {
        public bool ok;
        public string source;
        public string suiteId;
        public string[] selectedCaseIds = Array.Empty<string>();
        public List<AutomationDiagnosticV1> diagnostics = new();
    }

    public static class AutomationCatalogV1
    {
        internal static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

        public static AutomationCatalogValidationResultV1 Validate(AutomationCatalogDescriptorV1 catalog)
        {
            var result = new AutomationCatalogValidationResultV1();
            if (catalog == null)
            {
                Add(result.diagnostics, "CATALOG_REQUIRED", "Catalog is required.");
                return Finish(result);
            }

            if (catalog.version != 1)
                Add(result.diagnostics, "CATALOG_VERSION_UNSUPPORTED", "Only catalog version 1 is supported.");

            var cases = catalog.cases ?? Array.Empty<AutomationCaseDescriptorV1>();
            var caseById = new Dictionary<string, AutomationCaseDescriptorV1>(IdComparer);
            for (int index = 0; index < cases.Length; index++)
            {
                AutomationCaseDescriptorV1 item = cases[index];
                string id = NormalizeId(item?.id);
                if (string.IsNullOrEmpty(id))
                {
                    Add(result.diagnostics, "CASE_ID_REQUIRED", "Case ID is required.", string.Empty, index);
                    continue;
                }

                if (!caseById.TryAdd(id, item))
                    Add(result.diagnostics, "CASE_ID_DUPLICATE", "Case ID is duplicated.", id, index);
            }

            foreach (AutomationCaseDescriptorV1 item in cases.Where(item => item != null))
            {
                ValidateConstraintIds(item.id, "beforeCaseIds", item.beforeCaseIds, caseById, result.diagnostics);
                ValidateConstraintIds(item.id, "afterCaseIds", item.afterCaseIds, caseById, result.diagnostics);
            }

            var suites = catalog.suites ?? Array.Empty<AutomationSuiteDescriptorV1>();
            var suiteIds = new HashSet<string>(IdComparer);
            for (int index = 0; index < suites.Length; index++)
            {
                AutomationSuiteDescriptorV1 suite = suites[index];
                string id = NormalizeId(suite?.id);
                if (string.IsNullOrEmpty(id))
                {
                    Add(result.diagnostics, "SUITE_ID_REQUIRED", "Suite ID is required.", string.Empty, index);
                    continue;
                }

                if (!suiteIds.Add(id))
                    Add(result.diagnostics, "SUITE_ID_DUPLICATE", "Suite ID is duplicated.", id, index);

                string[] selected = suite.caseIds;
                if (selected == null || selected.Length == 0)
                {
                    Add(result.diagnostics, "SUITE_EMPTY", "Suite must select at least one Case.", id, index);
                    continue;
                }

                var seen = new HashSet<string>(IdComparer);
                for (int caseIndex = 0; caseIndex < selected.Length; caseIndex++)
                {
                    string caseId = NormalizeId(selected[caseIndex]);
                    if (string.IsNullOrEmpty(caseId))
                        Add(result.diagnostics, "SUITE_CASE_ID_REQUIRED", "Suite Case ID is required.", id, caseIndex);
                    else if (!caseById.ContainsKey(caseId))
                        Add(result.diagnostics, "SUITE_CASE_UNKNOWN", "Suite references an unknown Case.", caseId, caseIndex);
                    else if (!seen.Add(caseId))
                        Add(result.diagnostics, "SUITE_CASE_DUPLICATE", "Suite contains a duplicate Case.", caseId, caseIndex);
                }
            }

            return Finish(result);
        }

        private static void ValidateConstraintIds(
            string ownerId,
            string field,
            string[] ids,
            Dictionary<string, AutomationCaseDescriptorV1> caseById,
            List<AutomationDiagnosticV1> diagnostics)
        {
            if (ids == null)
                return;
            var seen = new HashSet<string>(IdComparer);
            for (int index = 0; index < ids.Length; index++)
            {
                string id = NormalizeId(ids[index]);
                if (string.IsNullOrEmpty(id))
                    Add(diagnostics, "CONSTRAINT_ID_REQUIRED", field + " contains an empty Case ID.", ownerId, index);
                else if (IdComparer.Equals(ownerId, id))
                    Add(diagnostics, "CONSTRAINT_SELF_REFERENCE", field + " cannot reference its own Case.", ownerId, index);
                else if (!caseById.ContainsKey(id))
                    Add(diagnostics, "CONSTRAINT_CASE_UNKNOWN", field + " references an unknown Case.", id, index);
                else if (!seen.Add(id))
                    Add(diagnostics, "CONSTRAINT_CASE_DUPLICATE", field + " contains a duplicate Case.", id, index);
            }
        }

        private static AutomationCatalogValidationResultV1 Finish(AutomationCatalogValidationResultV1 result)
        {
            result.ok = result.diagnostics.All(item => !IdComparer.Equals(item.severity, "error"));
            return result;
        }

        internal static void Add(
            List<AutomationDiagnosticV1> diagnostics,
            string code,
            string message,
            string subjectId = "",
            int index = -1)
        {
            diagnostics.Add(new AutomationDiagnosticV1
            {
                code = code,
                message = message,
                subjectId = subjectId ?? string.Empty,
                index = index,
            });
        }

        internal static string NormalizeId(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    public static class AutomationSelectionV1
    {
        public static AutomationSelectionResultV1 Analyze(
            AutomationCatalogDescriptorV1 catalog,
            AutomationSelectionRequestV1 request)
        {
            var result = new AutomationSelectionResultV1();
            AutomationCatalogValidationResultV1 validation = AutomationCatalogV1.Validate(catalog);
            result.diagnostics.AddRange(validation.diagnostics);
            if (!validation.ok)
                return Finish(result);

            if (request == null)
            {
                AutomationCatalogV1.Add(result.diagnostics, "SELECTION_REQUIRED", "A Suite or explicit Case list is required.");
                return Finish(result);
            }

            var caseById = (catalog.cases ?? Array.Empty<AutomationCaseDescriptorV1>())
                .ToDictionary(item => item.id.Trim(), item => item, AutomationCatalogV1.IdComparer);
            string[] requestedIds;
            if (request.caseIds != null)
            {
                result.source = "cases";
                if (request.caseIds.Length == 0)
                {
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_CASES_EMPTY", "Explicit Case selection cannot be empty.");
                    return Finish(result);
                }
                requestedIds = request.caseIds;
            }
            else
            {
                result.source = "suite";
                string suiteId = AutomationCatalogV1.NormalizeId(request.suiteId);
                if (string.IsNullOrEmpty(suiteId))
                {
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_REQUIRED", "A Suite or explicit Case list is required.");
                    return Finish(result);
                }

                AutomationSuiteDescriptorV1 suite = (catalog.suites ?? Array.Empty<AutomationSuiteDescriptorV1>())
                    .FirstOrDefault(item => AutomationCatalogV1.IdComparer.Equals(item.id, suiteId));
                if (suite == null)
                {
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_SUITE_UNKNOWN", "Selected Suite is unknown.", suiteId);
                    return Finish(result);
                }
                result.suiteId = suite.id;
                requestedIds = suite.caseIds;
            }

            var selected = new List<AutomationCaseDescriptorV1>(requestedIds.Length);
            var seen = new HashSet<string>(AutomationCatalogV1.IdComparer);
            for (int index = 0; index < requestedIds.Length; index++)
            {
                string id = AutomationCatalogV1.NormalizeId(requestedIds[index]);
                if (string.IsNullOrEmpty(id))
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_CASE_ID_REQUIRED", "Selected Case ID is required.", string.Empty, index);
                else if (!caseById.TryGetValue(id, out AutomationCaseDescriptorV1 descriptor))
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_CASE_UNKNOWN", "Selected Case is unknown.", id, index);
                else if (!seen.Add(id))
                    AutomationCatalogV1.Add(result.diagnostics, "SELECTION_CASE_DUPLICATE", "Selected Case is duplicated.", id, index);
                else
                    selected.Add(descriptor);
            }

            result.selectedCaseIds = selected.Select(item => item.id).ToArray();
            if (result.diagnostics.Any(item => item.severity == "error"))
                return Finish(result);

            ValidateOrder(selected, result.diagnostics);
            ValidateCycles(selected, result.diagnostics);
            return Finish(result);
        }

        private static void ValidateOrder(List<AutomationCaseDescriptorV1> selected, List<AutomationDiagnosticV1> diagnostics)
        {
            var positions = new Dictionary<string, int>(AutomationCatalogV1.IdComparer);
            for (int index = 0; index < selected.Count; index++)
                positions[selected[index].id] = index;

            for (int index = 0; index < selected.Count; index++)
            {
                AutomationCaseDescriptorV1 item = selected[index];
                if (item.mustBeLast && index != selected.Count - 1)
                    AutomationCatalogV1.Add(diagnostics, "SELECTION_MUST_BE_LAST", "Selected Case must be last.", item.id, index);

                foreach (string target in item.beforeCaseIds ?? Array.Empty<string>())
                {
                    if (positions.TryGetValue(target, out int targetIndex) && index >= targetIndex)
                        AutomationCatalogV1.Add(diagnostics, "SELECTION_ORDER_BEFORE", "Case violates a before constraint.", item.id, index);
                }
                foreach (string target in item.afterCaseIds ?? Array.Empty<string>())
                {
                    if (positions.TryGetValue(target, out int targetIndex) && index <= targetIndex)
                        AutomationCatalogV1.Add(diagnostics, "SELECTION_ORDER_AFTER", "Case violates an after constraint.", item.id, index);
                }
            }
        }

        private static void ValidateCycles(List<AutomationCaseDescriptorV1> selected, List<AutomationDiagnosticV1> diagnostics)
        {
            var selectedIds = new HashSet<string>(selected.Select(item => item.id), AutomationCatalogV1.IdComparer);
            var edges = selected.ToDictionary(
                item => item.id,
                _ => new HashSet<string>(AutomationCatalogV1.IdComparer),
                AutomationCatalogV1.IdComparer);
            foreach (AutomationCaseDescriptorV1 item in selected)
            {
                foreach (string target in item.beforeCaseIds ?? Array.Empty<string>())
                    if (selectedIds.Contains(target)) edges[item.id].Add(target);
                foreach (string target in item.afterCaseIds ?? Array.Empty<string>())
                    if (selectedIds.Contains(target)) edges[target].Add(item.id);
            }

            var states = new Dictionary<string, int>(AutomationCatalogV1.IdComparer);
            foreach (string id in selectedIds)
            {
                if (HasCycle(id, edges, states))
                {
                    AutomationCatalogV1.Add(diagnostics, "SELECTION_CONSTRAINT_CYCLE", "Selected Case constraints contain a cycle.", id);
                    return;
                }
            }
        }

        private static bool HasCycle(
            string id,
            Dictionary<string, HashSet<string>> edges,
            Dictionary<string, int> states)
        {
            if (states.TryGetValue(id, out int state))
                return state == 1;
            states[id] = 1;
            foreach (string target in edges[id])
                if (HasCycle(target, edges, states)) return true;
            states[id] = 2;
            return false;
        }

        private static AutomationSelectionResultV1 Finish(AutomationSelectionResultV1 result)
        {
            result.ok = result.diagnostics.All(item => item.severity != "error");
            return result;
        }
    }
}
