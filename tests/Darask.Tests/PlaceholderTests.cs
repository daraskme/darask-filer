using Xunit;

namespace Darask.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Solution_Builds_And_Tests_Run()
    {
        Assert.Equal("Darask.Ipc", Darask.Ipc.ProjectMarker.Name);
    }
}
