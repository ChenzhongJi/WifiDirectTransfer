using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class ReceiverPreferencesStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new ReceiverPreferencesStore(path);

            var saved = store.Save(
                new ReceiverPreferences
                {
                    SameNamePolicy = SameNamePolicy.Skip,
                    ClipboardSyncEnabled = true,
                    ClipboardConsentAcknowledged = true,
                    ReceiveDirectory = @"C:\Data\Inbox"
                });

            Assert.True(saved);

            var loaded = new ReceiverPreferencesStore(path).Load();

            Assert.Equal(SameNamePolicy.Skip, loaded.SameNamePolicy);
            Assert.True(loaded.ClipboardSyncEnabled);
            Assert.True(loaded.ClipboardConsentAcknowledged);
            Assert.Equal(@"C:\Data\Inbox", loaded.ReceiveDirectory);
        });
    }

    [Fact]
    public void Load_OldSettingsWithoutDirectory_ReturnsNullDirectory()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(
                path,
                """
                {
                  "SameNamePolicy": "Overwrite",
                  "ClipboardSyncEnabled": true,
                  "ClipboardConsentAcknowledged": false
                }
                """);

            var loaded = new ReceiverPreferencesStore(path).Load();

            Assert.Equal(SameNamePolicy.Overwrite, loaded.SameNamePolicy);
            Assert.True(loaded.ClipboardSyncEnabled);
            Assert.Null(loaded.ReceiveDirectory);
        });
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "not-json");

            var loaded = new ReceiverPreferencesStore(path).Load();

            Assert.Equal(SameNamePolicy.AutoRename, loaded.SameNamePolicy);
            Assert.False(loaded.ClipboardSyncEnabled);
            Assert.Null(loaded.ReceiveDirectory);
        });
    }

    private static void InTempDirectory(Action<string> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wdt-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            test(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理忽略文件占用。
            }
        }
    }
}
