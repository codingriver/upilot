using NUnit.Framework;

namespace CodingRiver.UPilot.SelectionEvidence
{
    [Category("P1Inherited")]
    public abstract class SelectionBase { }

    [TestFixture, Category("P1AssemblyA"), Explicit("P1 cross-assembly selection acceptance only.")]
    public class SharedFixture : SelectionBase
    {
        [Test, Category("P1Leaf")]
        public void Leaf()
        {
            Assert.That(GetType().Assembly.GetName().Name, Is.EqualTo("UPilot.SelectionA.Tests"));
            TestContext.Progress.WriteLine("P1 selection executed in UPilot.SelectionA.Tests");
        }
    }
}
