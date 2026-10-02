namespace DatesErp.Tests;
public class TodayOrdersTests
{
    [Fact]
    public void Approved_Today_Only_Immutable_Order_And_Execution_End_To_End()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        int steps = 0;
        TodayOrdersScenarios.Run(host.Services, (ok, message) => { Assert.True(ok, message); steps++; });
        Assert.True(steps >= 50);
    }
}
