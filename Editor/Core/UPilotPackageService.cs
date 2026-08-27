// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class PackageAddMessage     { public PackageAddPayload payload; }
    [Serializable] public class PackageAddPayload     { public string packageName = ""; public string version = ""; }

    [Serializable] public class PackageRemoveMessage  { public PackageRemovePayload payload; }
    [Serializable] public class PackageRemovePayload  { public string packageName = ""; }

    [Serializable] public class PackageSearchMessage  { public PackageSearchPayload payload; }
    [Serializable] public class PackageSearchPayload  { public string query = ""; }

    [Serializable]
    public class PackageInfoPayload
    {
        public string packageName;
        public string version;
        public string displayName;
        public string description;
        public string source;
    }

    [Serializable]
    public class PackageAddResultPayload
    {
        public string packageName;
        public string version;
        public string status;
    }

    [Serializable]
    public class PackageListResultPayload
    {
        public List<PackageInfoPayload> packages = new List<PackageInfoPayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotPackageService
    {
        private readonly UPilotBridge _bridge;

        public UPilotPackageService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("package.add",    HandleAddAsync);
            _bridge.Router.Register("package.remove", HandleRemoveAsync);
            _bridge.Router.Register("package.list",   HandleListAsync);
            _bridge.Router.Register("package.search", HandleSearchAsync);
        }

        // ── package.add ─────────────────────────────────────────────────────────

        private async Task HandleAddAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageAddMessage>(json);
            var p   = msg?.payload ?? new PackageAddPayload();

            if (string.IsNullOrEmpty(p.packageName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "packageName is required.", token, "package.add");
                return;
            }

            string identifier = string.IsNullOrEmpty(p.version) ? p.packageName : $"{p.packageName}@{p.version}";
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);
            opCtx?.Step("准备添加包", identifier);

            PackageAddResultPayload result;
            try
            {
                result = await RunPackageRequestAsync(
                    id,
                    "等待包管理器完成",
                    () => Client.Add(identifier),
                    request => new PackageAddResultPayload
                    {
                        packageName = request.Result?.name ?? p.packageName,
                        version     = request.Result?.version ?? p.version,
                        status      = "ok",
                    },
                    token);
            }
            catch (PackageRequestFailedException ex)
            {
                await _bridge.SendErrorAsync(id, "PACKAGE_ADD_FAILED", ex.Message, token, "package.add");
                return;
            }

            await _bridge.SendResultAsync(id, "package.add", result, token);
        }

        // ── package.remove ──────────────────────────────────────────────────────

        private async Task HandleRemoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageRemoveMessage>(json);
            var p   = msg?.payload ?? new PackageRemovePayload();

            if (string.IsNullOrEmpty(p.packageName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "packageName is required.", token, "package.remove");
                return;
            }

            try
            {
                await RunPackageRequestAsync(
                    id,
                    "等待包管理器完成",
                    () => Client.Remove(p.packageName),
                    _ => true,
                    token);
            }
            catch (PackageRequestFailedException ex)
            {
                await _bridge.SendErrorAsync(id, "PACKAGE_REMOVE_FAILED", ex.Message, token, "package.remove");
                return;
            }

            await _bridge.SendResultAsync(id, "package.remove", new GenericOkPayload { status = "ok" }, token);
        }

        // ── package.list ────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            PackageListResultPayload result;
            try
            {
                result = await RunPackageRequestAsync(
                    id,
                    "等待包管理器完成",
                    () => Client.List(true), // include dependencies
                    request => BuildPackageListPayload(request.Result),
                    token);
            }
            catch (PackageRequestFailedException ex)
            {
                await _bridge.SendErrorAsync(id, "PACKAGE_LIST_FAILED", ex.Message, token, "package.list");
                return;
            }

            await _bridge.SendResultAsync(id, "package.list", result, token);
        }

        // ── package.search ──────────────────────────────────────────────────────

        private async Task HandleSearchAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PackageSearchMessage>(json);
            var p   = msg?.payload ?? new PackageSearchPayload();

            if (string.IsNullOrEmpty(p.query))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "query is required.", token, "package.search");
                return;
            }

            PackageListResultPayload result;
            try
            {
                result = await RunPackageRequestAsync(
                    id,
                    "等待包管理器完成",
                    Client.SearchAll,
                    request => BuildPackageListPayload(request.Result),
                    token);
            }
            catch (PackageRequestFailedException ex)
            {
                await _bridge.SendErrorAsync(id, "PACKAGE_SEARCH_FAILED", ex.Message, token, "package.search");
                return;
            }

            await _bridge.SendResultAsync(id, "package.search", result, token);
        }

        private Task<TResult> RunPackageRequestAsync<TRequest, TResult>(
            string id,
            string waitingStep,
            Func<TRequest> startRequest,
            Func<TRequest, TResult> buildResult,
            CancellationToken token)
            where TRequest : Request
        {
            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);
            TRequest request = null;
            bool started = false;
            EditorApplication.CallbackFunction tick = null;

            void Cleanup()
            {
                if (tick != null)
                    EditorApplication.update -= tick;
            }

            tick = () =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        Cleanup();
                        tcs.TrySetCanceled(token);
                        return;
                    }

                    if (!started)
                    {
                        request = startRequest();
                        started = true;
                        opCtx?.Step(waitingStep);
                    }

                    if (request == null || !request.IsCompleted)
                        return;

                    Cleanup();
                    if (request.Status == StatusCode.Failure)
                    {
                        string errMsg = request.Error != null ? request.Error.message : "Unknown error";
                        tcs.TrySetException(new PackageRequestFailedException(errMsg));
                        return;
                    }

                    tcs.TrySetResult(buildResult(request));
                }
                catch (Exception ex)
                {
                    Cleanup();
                    tcs.TrySetException(ex);
                }
            };

            _bridge.EnqueueTracked(id, () =>
            {
                if (token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(token);
                    return;
                }

                EditorApplication.update += tick;
                tick();
            });

            return tcs.Task;
        }

        private static PackageListResultPayload BuildPackageListPayload(IEnumerable<UnityEditor.PackageManager.PackageInfo> packages)
        {
            var result = new PackageListResultPayload();
            if (packages == null)
                return result;

            foreach (var pkg in packages)
            {
                result.packages.Add(new PackageInfoPayload
                {
                    packageName = pkg.name,
                    version     = pkg.version,
                    displayName = pkg.displayName,
                    description = pkg.description ?? "",
                    source      = pkg.source.ToString(),
                });
            }

            return result;
        }

        private sealed class PackageRequestFailedException : Exception
        {
            public PackageRequestFailedException(string message) : base(message) { }
        }
    }
}
