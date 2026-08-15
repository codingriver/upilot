using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace CodingRiver.UPilot.Flow
{
    public sealed class UPilotFlowSchemaMigrationTests
    {
        [Test]
        public void LegacyYamlMigrationIsDryRunFirstAndIdempotent()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UPilotFlowMigration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "legacy.yaml");
            File.WriteAllText(path, "name: Legacy\nsteps:\n  - action: wait\n    duration: 10ms\n");

            try
            {
                var service = new UPilotFlowMigrationService();
                var preview = service.Migrate(path, dryRun: true);
                Assert.That(preview.changed, Is.True);
                Assert.That(File.ReadAllText(path), Does.Not.StartWith("schemaVersion"));

                var migrated = service.Migrate(path, dryRun: false);
                Assert.That(migrated.error, Is.Null.Or.Empty);
                Assert.That(File.ReadAllText(path), Does.StartWith("schemaVersion: 2"));

                var repeated = service.Migrate(path, dryRun: false);
                Assert.That(repeated.changed, Is.False);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ActionDescriptorsGenerateSchemaMetadata()
        {
            var registry = new ActionRegistry();
            var click = registry.Descriptors.Single(item => item.Name == "click");
            Assert.That(click.TargetRequired, Is.True);
            Assert.That(click.DefaultTimeoutMs, Is.EqualTo(10000));
            Assert.That(UPilotFlowSchemaGenerator.GenerateJson(registry.Descriptors), Does.Contain("schemaVersion"));
        }
    }
}
