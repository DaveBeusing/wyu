using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Groundwork.Rendering.Textures;

internal static class CookOrbitDemoAssets
{
    private const int TargetSize = 512;
    private const int MipCount = 10;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Specify the Poly Haven download directory and the output directory.");
            return 2;
        }

        var limits = new TextureLimits(2 << 20, 2 << 20, 2 << 20, TargetSize, MipCount);
        foreach (var asset in new[] { "rock_01", "rocks_ground_05" })
        {
            CookMap(asset, "diff", "diff", TextureColorSpace.Srgb, TextureSemantic.Color, args[0], args[1], limits);
            CookMap(asset, "nor_dx", "normal", TextureColorSpace.Linear, TextureSemantic.Normal, args[0], args[1], limits);
            CookMap(asset, "rough", "rough", TextureColorSpace.Linear, TextureSemantic.Data, args[0], args[1], limits);
        }
        return 0;
    }

    private static void CookMap(string asset, string suffix, string cookedSuffix,
        TextureColorSpace colorSpace, TextureSemantic semantic, string input, string output, TextureLimits limits)
    {
        using var png = File.OpenRead(Path.Combine(input, $"{asset}_{suffix}_1k.png"));
        var bitmap = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        if (bgra.PixelWidth != TargetSize * 2 || bgra.PixelHeight != TargetSize * 2)
            throw new InvalidDataException($"{asset} {suffix}: expected a 1024 × 1024 source image.");

        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var tga = new byte[18 + TargetSize * TargetSize * 4];
        tga[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12), TargetSize);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14), TargetSize);
        tga[16] = 32;
        tga[17] = 0x28;

        for (int y = 0; y < TargetSize; y++)
        {
            for (int x = 0; x < TargetSize; x++)
            {
                int source = y * 2 * stride + x * 8;
                int destination = 18 + (y * TargetSize + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int index = source + channel;
                    byte a = pixels[index];
                    byte b = pixels[index + 4];
                    byte c = pixels[index + stride];
                    byte d = pixels[index + stride + 4];
                    tga[destination + channel] = colorSpace == TextureColorSpace.Srgb
                        ? ToSrgb((ToLinear(a) + ToLinear(b) + ToLinear(c) + ToLinear(d)) / 4)
                        : (byte)((a + b + c + d + 2) / 4);
                }
                if (semantic == TextureSemantic.Normal)
                    NormalizeNormal(tga.AsSpan(destination, 3));
                tga[destination + 3] = byte.MaxValue;
            }
        }

        var error = TextureCooker.CookTga32(tga, new TextureCookRecipe(colorSpace, semantic, MipCount), limits, out var cooked);
        if (error != TextureCookError.None || cooked is null)
            throw new InvalidDataException($"{asset} {suffix}: {error}.");
        error = TextureCooker.ReadCooked(cooked.CanonicalBytes, cooked.AssetId, limits, out _);
        if (error != TextureCookError.None)
            throw new InvalidDataException($"{asset} {suffix} round-trip: {error}.");

        var filename = $"{asset}_{cookedSuffix}.gwtx";
        File.WriteAllBytes(Path.Combine(output, filename), cooked.CanonicalBytes);
        Console.WriteLine($"{filename}: {cooked.AssetId}, {cooked.Mips.Length} mips, {cooked.CanonicalBytes.Length} bytes");
    }

    private static double ToLinear(byte encoded)
    {
        double sample = encoded / 255d;
        return sample <= 0.04045 ? sample / 12.92 : Math.Pow((sample + 0.055) / 1.055, 2.4);
    }

    private static byte ToSrgb(double linear) =>
        Quantize(linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055);

    private static byte Quantize(double sample) => (byte)Math.Round(Math.Clamp(sample, 0, 1) * byte.MaxValue);

    private static void NormalizeNormal(Span<byte> bgra)
    {
        double blue = bgra[0] / 127.5 - 1;
        double green = bgra[1] / 127.5 - 1;
        double red = bgra[2] / 127.5 - 1;
        double length = Math.Sqrt(blue * blue + green * green + red * red);
        if (length > 0)
        {
            bgra[0] = Quantize(blue / length * 0.5 + 0.5);
            bgra[1] = Quantize(green / length * 0.5 + 0.5);
            bgra[2] = Quantize(red / length * 0.5 + 0.5);
        }
    }
}
