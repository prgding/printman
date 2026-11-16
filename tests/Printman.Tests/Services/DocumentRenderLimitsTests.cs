using Printman.Services.Renderers;

namespace Printman.Tests.Services;

[TestClass]
public sealed class DocumentRenderLimitsTests
{
    [TestMethod]
    [DataRow(0.0, 10.0)]
    [DataRow(-1.0, 10.0)]
    [DataRow(20001.0, 1.0)]
    [DataRow(10000.0, 10000.0)]
    [DataRow(double.PositiveInfinity, 100.0)]
    [DataRow(double.NaN, 100.0)]
    public void OversizedOrInvalidDimensionsAreRejected(double width, double height) =>
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentRenderLimits.ValidateDimensions(width, height));

    [TestMethod]
    public void OfficePagesAndBoundaryValuesAreAccepted()
    {
        DocumentRenderLimits.ValidateDimensions(2480, 3508);
        DocumentRenderLimits.ValidateDimensions(20000, 2000);
        DocumentRenderLimits.ValidatePageCount(1);
        DocumentRenderLimits.ValidatePageCount(1000);
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentRenderLimits.ValidatePageCount(1001));
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentRenderLimits.ValidatePageCount(0));
    }
}
