using System.Drawing;
using System.IO;
using System.Reflection;

internal static class Brand
{
    public static Icon LoadIcon()
    {
        return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
    }

    public static Image LoadMark()
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DLI.BrandMark"))
        using (Image image = Image.FromStream(stream))
            return new Bitmap(image);
    }
}
