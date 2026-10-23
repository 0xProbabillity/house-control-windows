using QRCoder;

namespace HouseControl.Core.SmartLife;

/// <summary>QR codes for the Smart Life sign-in (and anything else the web UI shows as an image).</summary>
public static class QrPng
{
    /// <summary>
    /// <paramref name="content"/> as a black-on-white PNG, ten pixels per module and error
    /// correction level M, like the Mac app's code.
    /// </summary>
    public static byte[] Png(string content, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>The PNG as a <c>data:image/png;base64,…</c> URL, ready for an <c>img</c> in the web UI.</summary>
    public static string DataUrl(string content) => "data:image/png;base64," + Convert.ToBase64String(Png(content));
}
