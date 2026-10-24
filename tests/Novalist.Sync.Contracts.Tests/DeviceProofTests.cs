using System.Text;

namespace Novalist.Sync.Contracts.Tests;

public sealed class DeviceProofTests
{
    [Fact]
    public void ProofFramesUseFixedDomainsAndBigEndianIdentities()
    {
        var user = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var device = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
        var nonce = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
        var signing = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var receiving = Convert.FromHexString("202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");
        var keys = new DevicePublicKeys(device, signing, receiving);
        byte[] registration = [.. Encoding.UTF8.GetBytes("novalist/v1/device-register"), 0, 21, .. Encoding.UTF8.GetBytes("https://sync.example/"), .. Convert.FromHexString(
            "00112233445566778899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0F000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F")];
        Assert.Equal(registration, DeviceProof.EncodeRegistration("https://sync.example/", user, keys));
        byte[] connection = [.. Encoding.UTF8.GetBytes("novalist/v1/device-connect"), 0, 21, .. Encoding.UTF8.GetBytes("https://sync.example/"), .. Convert.FromHexString(
            "00112233445566778899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA99887766554433221100")];
        Assert.Equal(connection, DeviceProof.EncodeConnection(new(user, device, nonce, "https://sync.example/")));
        byte[] linking = [.. Encoding.UTF8.GetBytes("novalist/v1/device-link"), .. Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"), .. registration];
        Assert.Equal(linking, DeviceProof.EncodeLink("https://sync.example/", user, nonce, keys));
        Assert.Throws<FormatException>(() => DeviceProof.EncodeRegistration("https://sync.example/", user, keys with { SigningPublicKey = [] }));
        Assert.Throws<FormatException>(() => DeviceProof.EncodeConnection(new(user, device, Guid.Empty, "https://sync.example/")));
        Assert.Throws<FormatException>(() => DeviceProof.EncodeLink("https://sync.example/", user, Guid.Empty, keys));
    }
}
