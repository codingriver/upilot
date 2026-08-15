// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class MaterialCreateMessage   { public MaterialCreatePayload payload; }
    [Serializable] public class MaterialCreatePayload   { public string materialPath = ""; public string shaderName = "Standard"; }

    [Serializable] public class MaterialModifyMessage   { public MaterialModifyPayload payload; }
    [Serializable] public class MaterialModifyPayload   { public string materialPath = ""; public string properties = ""; }

    [Serializable] public class MaterialAssignMessage   { public MaterialAssignPayload payload; }
    [Serializable] public class MaterialAssignPayload   { public ulong targetGameObjectId; public string materialPath = ""; public int materialIndex; }

    [Serializable] public class MaterialGetMessage      { public MaterialGetPayload payload; }
    [Serializable] public class MaterialGetPayload      { public string materialPath = ""; }

    [Serializable]
    public class MaterialInfoPayload
    {
        public string materialPath;
        public string shaderName;
        public ulong  instanceId;
        public List<MaterialPropertyInfoPayload> properties = new List<MaterialPropertyInfoPayload>();
    }

    [Serializable]
    public class MaterialPropertyInfoPayload
    {
        public string name;
        public string type;   // Float, Color, Vector, Texture, Int
        public string value;
    }

    [Serializable]
    public class MaterialCreateResultPayload
    {
        public string materialPath;
        public ulong  instanceId;
        public string shaderName;
    }

    [Serializable]
    public class ShaderListResultPayload
    {
        public List<string> shaders = new List<string>();
    }

    [Serializable] public class ShaderInspectMessage { public ShaderInspectPayload payload; }
    [Serializable] public class ShaderInspectPayload { public string assetPath = ""; public bool includeWarnings = true; }

    [Serializable]
    public class ShaderDiagnosticMessagePayload
    {
        public string severity;
        public string message;
        public int line;
        public string platform;
        public string file;
    }

    [Serializable]
    public class ShaderDiagnosticResultPayload
    {
        public string assetPath;
        public string shaderName;
        public ulong instanceId;
        public bool imported;
        public bool supported;
        public int propertyCount;
        public int messageCount;
        public int errorCount;
        public int warningCount;
        public List<string> dependencies = new List<string>();
        public List<ShaderDiagnosticMessagePayload> messages = new List<ShaderDiagnosticMessagePayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotMaterialService
    {
        private readonly UPilotBridge _bridge;

        public UPilotMaterialService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("material.create",  HandleCreateAsync);
            _bridge.Router.Register("material.modify",  HandleModifyAsync);
            _bridge.Router.Register("material.assign",  HandleAssignAsync);
            _bridge.Router.Register("material.get",     HandleGetAsync);
            _bridge.Router.Register("shader.list",      HandleShaderListAsync);
            _bridge.Router.Register("shader.inspect",   HandleShaderInspectAsync);
            _bridge.Router.Register("shader.checkErrors", HandleShaderCheckErrorsAsync);
        }

        private Task HandleShaderInspectAsync(string id, string json, CancellationToken token)
        {
            return HandleShaderDiagnosticsAsync(id, json, token, "shader.inspect", includeMessages: true);
        }

        private Task HandleShaderCheckErrorsAsync(string id, string json, CancellationToken token)
        {
            return HandleShaderDiagnosticsAsync(id, json, token, "shader.checkErrors", includeMessages: true);
        }

        private async Task HandleShaderDiagnosticsAsync(string id, string json, CancellationToken token, string command, bool includeMessages)
        {
            var msg = JsonUtility.FromJson<ShaderInspectMessage>(json);
            var p = msg?.payload ?? new ShaderInspectPayload();
            if (string.IsNullOrWhiteSpace(p.assetPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "assetPath is required.", token, command);
                return;
            }

            var tcs = new TaskCompletionSource<ShaderDiagnosticResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    string path = p.assetPath.Replace('\\', '/');
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader == null) throw new InvalidOperationException($"Shader asset not found: {path}");
                    var result = new ShaderDiagnosticResultPayload
                    {
                        assetPath = path,
                        shaderName = shader.name,
                        instanceId = UPilotEntityIds.ToWireId(shader),
                        imported = AssetImporter.GetAtPath(path) != null,
                        supported = shader.isSupported,
                        propertyCount = ShaderUtil.GetPropertyCount(shader),
                        dependencies = AssetDatabase.GetDependencies(path, false).Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)).ToList(),
                    };
                    if (includeMessages) ReadShaderMessages(shader, p.includeWarnings, result);
                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try { await _bridge.SendResultAsync(id, command, await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "SHADER_DIAGNOSTICS_FAILED", ex.Message, token, command); }
        }

        internal static void ReadShaderMessages(Shader shader, bool includeWarnings, ShaderDiagnosticResultPayload result)
        {
            var method = typeof(ShaderUtil).GetMethod("GetShaderMessages", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Shader) }, null);
            var values = method?.Invoke(null, new object[] { shader }) as IEnumerable;
            if (values == null) return;
            foreach (var value in values)
            {
                string severity = ReadMember(value, "severity");
                bool warning = severity.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0;
                bool error = severity.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                if (warning) result.warningCount++;
                if (error) result.errorCount++;
                if (!includeWarnings && warning) continue;
                result.messages.Add(new ShaderDiagnosticMessagePayload
                {
                    severity = severity,
                    message = ReadMember(value, "message"),
                    line = ReadIntMember(value, "line"),
                    platform = ReadMember(value, "platform"),
                    file = ReadMember(value, "file"),
                });
            }
            result.messageCount = result.messages.Count;
        }

        private static string ReadMember(object value, string name)
        {
            if (value == null) return string.Empty;
            var type = value.GetType();
            object member = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
                ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
            return Convert.ToString(member) ?? string.Empty;
        }

        private static int ReadIntMember(object value, string name)
        {
            return int.TryParse(ReadMember(value, name), out int parsed) ? parsed : 0;
        }

        // ── material.create ─────────────────────────────────────────────────────

        private async Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialCreateMessage>(json);
            var p   = msg?.payload ?? new MaterialCreatePayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.create");
                return;
            }

            var tcs = new TaskCompletionSource<MaterialCreateResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var shader = Shader.Find(p.shaderName);
                    if (shader == null)
                    {
                        tcs.SetException(new Exception($"Shader not found: {p.shaderName}"));
                        return;
                    }

                    var mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, p.materialPath);
                    AssetDatabase.SaveAssets();

                    tcs.SetResult(new MaterialCreateResultPayload
                    {
                        materialPath = p.materialPath,
                        instanceId   = UPilotEntityIds.ToWireId(mat),
                        shaderName   = shader.name,
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "material.create", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_CREATE_FAILED", ex.Message, token, "material.create");
            }
        }

        // ── material.modify ─────────────────────────────────────────────────────

        private async Task HandleModifyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialModifyMessage>(json);
            var p   = msg?.payload ?? new MaterialModifyPayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.modify");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    // Parse properties JSON
                    if (!string.IsNullOrEmpty(p.properties))
                    {
                        var props = UPilotComponentService.ParseSimpleJson(p.properties);
                        foreach (var kv in props)
                        {
                            ApplyMaterialProperty(mat, kv.Key, kv.Value);
                        }
                    }

                    EditorUtility.SetDirty(mat);
                    AssetDatabase.SaveAssets();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "material.modify", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_MODIFY_FAILED", ex.Message, token, "material.modify");
            }
        }

        // ── material.assign ─────────────────────────────────────────────────────

        private async Task HandleAssignAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialAssignMessage>(json);
            var p   = msg?.payload ?? new MaterialAssignPayload();

            if (p.targetGameObjectId == 0 || string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "targetGameObjectId and materialPath are required.", token, "material.assign");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var go = UPilotEntityIds.GameObjectFromWireId(p.targetGameObjectId);
                    if (go == null)
                    {
                        tcs.SetException(new Exception($"GameObject not found: {p.targetGameObjectId}"));
                        return;
                    }

                    var renderer = go.GetComponent<Renderer>();
                    if (renderer == null)
                    {
                        tcs.SetException(new Exception($"No Renderer component on: {go.name}"));
                        return;
                    }

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    var mats = renderer.sharedMaterials;
                    if (p.materialIndex < 0 || p.materialIndex >= mats.Length)
                    {
                        tcs.SetException(new Exception($"Material index {p.materialIndex} out of range [0, {mats.Length - 1}]"));
                        return;
                    }

                    Undo.RecordObject(renderer, "Assign Material");
                    mats[p.materialIndex] = mat;
                    renderer.sharedMaterials = mats;

                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "material.assign", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_ASSIGN_FAILED", ex.Message, token, "material.assign");
            }
        }

        // ── material.get ────────────────────────────────────────────────────────

        private async Task HandleGetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialGetMessage>(json);
            var p   = msg?.payload ?? new MaterialGetPayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.get");
                return;
            }

            var tcs = new TaskCompletionSource<MaterialInfoPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    var info = new MaterialInfoPayload
                    {
                        materialPath = p.materialPath,
                        shaderName   = mat.shader != null ? mat.shader.name : "Unknown",
                        instanceId   = UPilotEntityIds.ToWireId(mat),
                    };

                    // Read shader properties
                    if (mat.shader != null)
                    {
                        int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                        for (int i = 0; i < propCount; i++)
                        {
                            string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                            var propType = ShaderUtil.GetPropertyType(mat.shader, i);
                            string typeStr;
                            string valueStr;

                            switch (propType)
                            {
                                case ShaderUtil.ShaderPropertyType.Color:
                                    typeStr  = "Color";
                                    var c = mat.GetColor(propName);
                                    valueStr = $"({c.r},{c.g},{c.b},{c.a})";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Vector:
                                    typeStr  = "Vector";
                                    var v = mat.GetVector(propName);
                                    valueStr = $"({v.x},{v.y},{v.z},{v.w})";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Float:
                                case ShaderUtil.ShaderPropertyType.Range:
                                    typeStr  = "Float";
                                    valueStr = mat.GetFloat(propName).ToString("G");
                                    break;
                                case ShaderUtil.ShaderPropertyType.TexEnv:
                                    typeStr = "Texture";
                                    var tex = mat.GetTexture(propName);
                                    valueStr = tex != null ? AssetDatabase.GetAssetPath(tex) : "";
                                    break;
                                default:
                                    typeStr  = propType.ToString();
                                    valueStr = "";
                                    break;
                            }

                            info.properties.Add(new MaterialPropertyInfoPayload
                            {
                                name  = propName,
                                type  = typeStr,
                                value = valueStr,
                            });
                        }
                    }

                    tcs.SetResult(info);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "material.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_GET_FAILED", ex.Message, token, "material.get");
            }
        }

        // ── shader.list ─────────────────────────────────────────────────────────

        private async Task HandleShaderListAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<ShaderListResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new ShaderListResultPayload();

                    // Get all shader names available in the project
                    var shaderInfo = ShaderUtil.GetAllShaderInfo();
                    foreach (var si in shaderInfo)
                    {
                        if (!string.IsNullOrEmpty(si.name) && !si.name.StartsWith("Hidden/"))
                            result.shaders.Add(si.name);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "shader.list", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SHADER_LIST_FAILED", ex.Message, token, "shader.list");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void ApplyMaterialProperty(Material mat, string propName, string value)
        {
            if (!mat.HasProperty(propName)) return;

            // Try to detect property type
            int propIdx = -1;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(mat.shader); i++)
            {
                if (ShaderUtil.GetPropertyName(mat.shader, i) == propName)
                {
                    propIdx = i;
                    break;
                }
            }

            if (propIdx < 0) return;

            var propType = ShaderUtil.GetPropertyType(mat.shader, propIdx);
            switch (propType)
            {
                case ShaderUtil.ShaderPropertyType.Color:
                    if (TryParseColor(value, out var color))
                        mat.SetColor(propName, color);
                    break;
                case ShaderUtil.ShaderPropertyType.Vector:
                    if (TryParseVector4(value, out var vec))
                        mat.SetVector(propName, vec);
                    break;
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out float f))
                        mat.SetFloat(propName, f);
                    break;
                case ShaderUtil.ShaderPropertyType.TexEnv:
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                    mat.SetTexture(propName, tex); // null clears it
                    break;
            }
        }

        private static bool TryParseColor(string s, out Color color)
        {
            color = Color.white;
            s = s.Trim().Trim('(', ')');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float b)) return false;
            float a = 1f;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out a);
            color = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseVector4(string s, out Vector4 vec)
        {
            vec = Vector4.zero;
            s = s.Trim().Trim('(', ')');
            var parts = s.Split(',');
            if (parts.Length < 2) return false;
            float x = 0, y = 0, z = 0, w = 0;
            if (parts.Length > 0) float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out x);
            if (parts.Length > 1) float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out y);
            if (parts.Length > 2) float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out z);
            if (parts.Length > 3) float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out w);
            vec = new Vector4(x, y, z, w);
            return true;
        }
    }
}
