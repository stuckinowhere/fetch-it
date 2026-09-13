using FetchIt.Services;

namespace FetchIt.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void Second_claim_is_rejected()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "fetchit-lock-" + Guid.NewGuid().ToString("N"));
        var activate = @"Local\WasdFetchIt-test-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryOwn(lockPath, activate, out var first));
        using (first)
        {
            Assert.False(SingleInstance.TryOwn(lockPath, activate, out var second));
            Assert.Null(second);
        }

        Assert.True(SingleInstance.TryOwn(lockPath, activate, out var again));
        again!.Dispose();
        File.Delete(lockPath);
    }

    [Fact]
    public void Signal_wakes_the_owner()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "fetchit-lock-" + Guid.NewGuid().ToString("N"));
        var activate = @"Local\WasdFetchIt-test-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryOwn(lockPath, activate, out var owner));
        using (owner)
        {
            var seen = new ManualResetEventSlim(false);
            owner!.Watch(seen.Set);
            SingleInstance.Signal(activate);
            Assert.True(seen.Wait(TimeSpan.FromSeconds(2)));
        }

        File.Delete(lockPath);
    }
}
