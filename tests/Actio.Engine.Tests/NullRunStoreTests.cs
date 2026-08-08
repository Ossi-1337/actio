using Actio.Engine.Runs;

namespace Actio.Engine.Tests;

public sealed class NullRunStoreTests
{
    [Fact]
    public async Task CreateStepEnvironmentFilesAsync_IsolatesStoreInstances()
    {
        var firstStore = new NullRunStore();
        var secondStore = new NullRunStore();

        var first = await firstStore.CreateStepEnvironmentFilesAsync("run", "job", 0, "step");
        var second = await secondStore.CreateStepEnvironmentFilesAsync("run", "job", 0, "step");

        try
        {
            Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
            Assert.True(File.Exists(first.EnvironmentFilePath));
            Assert.True(File.Exists(second.EnvironmentFilePath));
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode groupOrOtherAccess = UnixFileMode.GroupRead |
                    UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite |
                    UnixFileMode.OtherExecute;
                Assert.Equal(
                    0,
                    (int)(File.GetUnixFileMode(firstStore.EnvironmentFileScopePath) & groupOrOtherAccess));
            }
        }
        finally
        {
            firstStore.CleanupEnvironmentFiles("run");
            secondStore.CleanupEnvironmentFiles("run");
        }

        Assert.False(Directory.Exists(firstStore.EnvironmentFileScopePath));
        Assert.False(Directory.Exists(secondStore.EnvironmentFileScopePath));
    }

    [Fact]
    public async Task CleanupEnvironmentFiles_RemovesOnlyTheCompletedRun()
    {
        var store = new NullRunStore();
        var first = await store.CreateStepEnvironmentFilesAsync("first", "job", 0, "step");
        var second = await store.CreateStepEnvironmentFilesAsync("second", "job", 0, "step");

        try
        {
            store.CleanupEnvironmentFiles("first");

            Assert.False(Directory.Exists(Directory.GetParent(first.DirectoryPath)!.Parent!.FullName));
            Assert.True(File.Exists(second.EnvironmentFilePath));
            Assert.True(Directory.Exists(store.EnvironmentFileScopePath));
        }
        finally
        {
            store.CleanupEnvironmentFiles("first");
            store.CleanupEnvironmentFiles("second");
        }

        Assert.False(Directory.Exists(store.EnvironmentFileScopePath));
    }
}
