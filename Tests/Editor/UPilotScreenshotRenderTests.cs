// -----------------------------------------------------------------------
// UPilot Editor tests
// -----------------------------------------------------------------------

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotScreenshotRenderTests
    {
        [Test]
        public void CameraDescriptorUsesOneFixedNonDynamicColorDepthSize()
        {
            var descriptor = UPilotScreenshotService.BuildCameraRenderDescriptor(1280, 720);
            Assert.That(descriptor.width, Is.EqualTo(1280));
            Assert.That(descriptor.height, Is.EqualTo(720));
            Assert.That(descriptor.depthBufferBits, Is.GreaterThanOrEqualTo(24));
            Assert.That(descriptor.msaaSamples, Is.EqualTo(1));
            Assert.That(descriptor.useDynamicScale, Is.False);
            Assert.That(descriptor.useMipMap, Is.False);
        }

        [Test]
        public void FindCameraIncludesInactiveCameras()
        {
            var cameraName = "UPilotInactiveScreenshotCamera_" + System.Guid.NewGuid().ToString("N");
            var gameObject = new GameObject(cameraName);
            try
            {
                var camera = gameObject.AddComponent<Camera>();
                gameObject.SetActive(false);

                var findCamera = typeof(UPilotScreenshotService).GetMethod(
                    "FindCamera",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(findCamera, Is.Not.Null);
                Assert.That(findCamera.Invoke(null, new object[] { cameraName }), Is.SameAs(camera));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RepeatedOddAndEvenCameraCapturesKeepColorDepthDimensionsEqual()
        {
            var gameObject = new GameObject("UPilotScreenshotCamera");
            try
            {
                var camera = gameObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta;
                camera.allowDynamicResolution = true;

                for (var index = 0; index < 10; index++)
                {
                    var width = index % 2 == 0 ? 127 : 128;
                    var height = index % 2 == 0 ? 72 : 73;
                    var capture = UPilotScreenshotService.RenderCamera(camera, width, height, "png", 75);
                    Assert.That(capture.Bytes, Is.Not.Null.And.Not.Empty);
                    Assert.That(capture.RequestedWidth, Is.EqualTo(width));
                    Assert.That(capture.RequestedHeight, Is.EqualTo(height));
                    Assert.That(capture.ColorWidth, Is.EqualTo(width));
                    Assert.That(capture.ColorHeight, Is.EqualTo(height));
                    Assert.That(capture.DepthWidth, Is.EqualTo(capture.ColorWidth));
                    Assert.That(capture.DepthHeight, Is.EqualTo(capture.ColorHeight));
                    Assert.That(camera.allowDynamicResolution, Is.True);
                    Assert.That(camera.targetTexture, Is.Null);
                }

                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
