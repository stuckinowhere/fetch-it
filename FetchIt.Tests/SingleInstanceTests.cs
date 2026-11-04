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
    public void Dispose_twice_is_safe_and_frees_the_lock()
    {
        // The updater disposes early to hand the lock to the restarted app;
        // Program.Main's using block disposes again on exit.
        var lockPath = Path.Combine(Path.GetTempPath(), "fetchit-lock-" + Guid.NewGuid().ToString("N"));
        var activate = @"Local\WasdFetchIt-test-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryOwn(lockPath, activate, out var owner));
        owner!.Watch(() => { });
        owner.Dispose();
        owner.Dispose();

        Assert.True(SingleInstance.TryOwn(lockPath, activate, out var next));
        next!.Dispose();
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
