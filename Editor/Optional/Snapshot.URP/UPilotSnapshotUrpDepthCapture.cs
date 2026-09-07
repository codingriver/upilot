// -----------------------------------------------------------------------
// UPilot optional URP Snapshot integration
// -----------------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
#endif

namespace CodingRiver.UPilot
{
    /// <summary>
    /// Injects one temporary URP pass for a single Camera.Render call. The core
    /// Snapshot assembly discovers this provider by reflection, so Built-in
    /// projects do not require a URP assembly reference.
    /// </summary>
    public static class UPilotSnapshotUrpDepthCapture
    {
        public static bool RenderDepthAndColor(
            Camera camera,
            RenderTexture rawDepth,
            RenderTexture linearDepth,
            Material depthMaterial)
        {
            if (camera == null || rawDepth == null || linearDepth == null || depthMaterial == null)
                return false;
            var additionalData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (additionalData == null || additionalData.scriptableRenderer == null)
                return false;

            var pass = new SnapshotDepthPass(rawDepth, linearDepth, depthMaterial);
            Action<ScriptableRenderContext, Camera> enqueue = (context, renderingCamera) =>
            {
                if (renderingCamera == camera)
                    additionalData.scriptableRenderer.EnqueuePass(pass);
            };
            RenderPipelineManager.beginCameraRendering += enqueue;
            try
            {
                camera.Render();
                return pass.Recorded;
            }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= enqueue;
                pass.Dispose();
            }
        }

        private sealed class SnapshotDepthPass : ScriptableRenderPass, IDisposable
        {
            private static readonly int InputDepthId = Shader.PropertyToID("_UPilotDepthTexture");
            private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
            private readonly RenderTexture _rawDepth;
            private readonly RenderTexture _linearDepth;
            private readonly Material _material;
#if UNITY_6000_0_OR_NEWER
            private readonly RTHandle _rawHandle;
            private readonly RTHandle _linearHandle;
#endif

            public bool Recorded { get; private set; }

            public SnapshotDepthPass(
                RenderTexture rawDepth,
                RenderTexture linearDepth,
                Material material)
            {
                _rawDepth = rawDepth;
                _linearDepth = linearDepth;
                _material = material;
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                ConfigureInput(ScriptableRenderPassInput.Depth);
#if UNITY_6000_0_OR_NEWER
                _rawHandle = RTHandles.Alloc(rawDepth, "UPilot Snapshot Raw Depth");
                _linearHandle = RTHandles.Alloc(linearDepth, "UPilot Snapshot Linear Depth");
#endif
            }

#if !UNITY_6000_0_OR_NEWER
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var command = CommandBufferPool.Get("UPilot Snapshot Depth");
                try
                {
                    command.SetGlobalTexture(InputDepthId, CameraDepthId);
                    command.Blit(Texture2D.blackTexture, _rawDepth, _material, 0);
                    command.Blit(Texture2D.blackTexture, _linearDepth, _material, 1);
                    context.ExecuteCommandBuffer(command);
                    Recorded = true;
                }
                finally
                {
                    CommandBufferPool.Release(command);
                }
            }
#endif

#if UNITY_6000_0_OR_NEWER
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                // Sample the live camera depth attachment. The copied
                // cameraDepthTexture can still be at its clear value when a
                // one-shot Camera.Render pass is inserted at this event.
                var sourceDepth = resources.activeDepthTexture;
                if (!sourceDepth.IsValid()) return;

                var rawInfo = BuildInfo(_rawDepth);
                var linearInfo = BuildInfo(_linearDepth);
                var raw = renderGraph.ImportTexture(_rawHandle, rawInfo);
                var linear = renderGraph.ImportTexture(_linearHandle, linearInfo);
                var rawParameters = new RenderGraphUtils.BlitMaterialParameters(
                    sourceDepth,
                    raw,
                    _material,
                    2)
                {
                    sourceTexturePropertyID = InputDepthId,
                };
                var linearParameters = new RenderGraphUtils.BlitMaterialParameters(
                    sourceDepth,
                    linear,
                    _material,
                    3)
                {
                    sourceTexturePropertyID = InputDepthId,
                };
                renderGraph.AddBlitPass(rawParameters, "UPilot Snapshot Raw Depth");
                renderGraph.AddBlitPass(linearParameters, "UPilot Snapshot Linear Depth");
                Recorded = true;
            }

            private static RenderTargetInfo BuildInfo(RenderTexture texture) => new RenderTargetInfo
            {
                width = texture.width,
                height = texture.height,
                volumeDepth = texture.volumeDepth,
                msaaSamples = 1,
                format = texture.graphicsFormat,
                bindMS = texture.bindTextureMS,
            };
#endif

            public void Dispose()
            {
#if UNITY_6000_0_OR_NEWER
                _rawHandle?.Release();
                _linearHandle?.Release();
#endif
            }
        }
    }
}
