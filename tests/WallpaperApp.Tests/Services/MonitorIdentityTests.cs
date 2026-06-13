using WallpaperApp.Services.Monitor;

namespace WallpaperApp.Tests.Services;

public class MonitorIdentityTests
{
    [Fact]
    public void GenerateKey_ReturnsConsistentHash()
    {
        var key1 = MonitorIdentity.GenerateKey("edid_hash_1", "HDMI");
        var key2 = MonitorIdentity.GenerateKey("edid_hash_1", "HDMI");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void GenerateKey_DifferentInputs_ReturnDifferentKeys()
    {
        var key1 = MonitorIdentity.GenerateKey("edid_hash_1", "HDMI");
        var key2 = MonitorIdentity.GenerateKey("edid_hash_2", "HDMI");
        var key3 = MonitorIdentity.GenerateKey("edid_hash_1", "DP");

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key2, key3);
    }

    [Fact]
    public void GenerateKey_ReturnsGuid()
    {
        var key = MonitorIdentity.GenerateKey("test", "USB");
        Assert.Equal(36, key.Length);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", key);
    }

    [Fact]
    public void GenerateKey_EmptyInputs_ReturnsValidGuid()
    {
        var key = MonitorIdentity.GenerateKey("", "");
        Assert.Equal(36, key.Length);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", key);
    }
}
