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
    public void GenerateKey_Returns64CharHex()
    {
        var key = MonitorIdentity.GenerateKey("test", "USB");
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9A-F]+$", key);
    }

    [Fact]
    public void GenerateKey_EmptyInputs_ReturnsValidHash()
    {
        var key = MonitorIdentity.GenerateKey("", "");
        Assert.Equal(64, key.Length);
    }
}
